"""
Remove plane / satellite trails from a sequence of fixed-tripod night-sky frames,
keeping the stars, so the frames can be stacked into a star-trail / circumpolar image.

Idea
----
1. Temporal reference: for frame i, take the per-pixel median of its neighbours
   (i-k .. i+k, i excluded). Trails are transient, so they vanish from this median.
2. Star-tolerant residual: dilate that reference with a small max-filter so stars,
   which only drift a few pixels between consecutive frames, are covered.
   residual = frame - dilated_reference  -> stars ~ 0, new bright things > 0.
3. Line detection: threshold the residual, then keep only *long straight* structures
   (probabilistic Hough, gap-tolerant for blinking aircraft lights). Isolated dots,
   noise and moved stars are rejected because they are not long lines.
4. Repair: inside a slightly widened mask of the detected lines, replace the pixels
   with the (non-dilated) temporal median of the neighbours. That is the real sky
   at this moment, stars included (a few pixels off at most), so nothing is smeared.

Usage
-----
    python remove_trails.py INPUT_DIR OUTPUT_DIR [--k 2] [--stack]

Outputs cleaned frames (same names), a *_mask.png per frame with detections,
and optionally star-trail stacks (max/lighten) before and after cleaning.
"""
import argparse
import glob
import os
from collections import OrderedDict

import cv2
import numpy as np


def load(path):
    img = cv2.imread(path, cv2.IMREAD_UNCHANGED)
    if img is None:
        raise IOError(f"cannot read {path}")
    return img


def to_gray_f32(img):
    g = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY) if img.ndim == 3 else img
    scale = 65535.0 if g.dtype == np.uint16 else 255.0
    return g.astype(np.float32) / scale


def detect_trails(frame_gray, ref_gray, args):
    """Return a uint8 mask (255 = trail) and the list of detected segments."""
    # Star-tolerant reference: a star that moved by <= dilate/2 px is still covered.
    k = args.star_dilate
    ref_dil = cv2.dilate(ref_gray, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k, k)))
    residual = frame_gray - ref_dil
    # Suppress single-pixel noise, keep thin lines.
    residual = cv2.GaussianBlur(residual, (3, 3), 0)

    # Robust noise estimate (MAD) -> adaptive threshold.
    med = np.median(residual)
    mad = np.median(np.abs(residual - med)) * 1.4826 + 1e-6
    binary = (residual > med + args.sigma * mad).astype(np.uint8) * 255

    # Remove isolated specks (moved stars, hot pixels) before Hough.
    n, labels, stats, _ = cv2.connectedComponentsWithStats(binary, connectivity=8)
    small = stats[:, cv2.CC_STAT_AREA] < args.min_blob
    small[0] = False
    binary[small[labels]] = 0

    h, w = binary.shape
    min_len = int(args.min_length * max(h, w))
    lines = cv2.HoughLinesP(binary, 1, np.pi / 720, threshold=max(20, min_len // 2),
                            minLineLength=min_len, maxLineGap=args.max_gap)

    mask = np.zeros_like(binary)
    kept = []
    if lines is not None:
        for x1, y1, x2, y2 in lines.reshape(-1, 4):  # shape differs across OpenCV versions
            # Verify: the residual along the segment must be consistently positive.
            length = int(np.hypot(x2 - x1, y2 - y1))
            xs = np.linspace(x1, x2, length).astype(int)
            ys = np.linspace(y1, y2, length).astype(int)
            on = binary[ys, xs] > 0
            # Also accept a 1px neighbour hit (anti-aliased thin trails).
            on |= binary[np.clip(ys + 1, 0, h - 1), xs] > 0
            on |= binary[np.clip(ys - 1, 0, h - 1), xs] > 0
            if on.mean() < args.min_fill:
                continue
            kept.append((x1, y1, x2, y2))
            cv2.line(mask, (int(x1), int(y1)), (int(x2), int(y2)), 255, args.width)

    if kept:
        # Grow over the faint halo / glow of the trail.
        mask = cv2.dilate(mask, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5)))
    return mask, kept


def repair(frame, ref_color, mask):
    m = cv2.GaussianBlur(mask.astype(np.float32) / 255.0, (7, 7), 0)
    if frame.ndim == 3:
        m = m[..., None]
    out = frame.astype(np.float32) * (1 - m) + ref_color.astype(np.float32) * m
    return out.round().astype(frame.dtype)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("input_dir")
    ap.add_argument("output_dir")
    ap.add_argument("--ext", default="jpg,jpeg,tif,tiff,png")
    ap.add_argument("--k", type=int, default=2, help="neighbours on each side for the temporal median")
    ap.add_argument("--star-dilate", type=int, default=7, help="px, must exceed star drift between frames")
    ap.add_argument("--sigma", type=float, default=2.5, help="detection threshold in noise sigmas (lower = fainter trails)")
    ap.add_argument("--min-blob", type=int, default=4, help="drop residual blobs smaller than this (px)")
    ap.add_argument("--min-length", type=float, default=0.03, help="min trail length, fraction of image size")
    ap.add_argument("--max-gap", type=int, default=25, help="px gap allowed in dashed (blinking) trails")
    ap.add_argument("--min-fill", type=float, default=0.35, help="fraction of a segment that must be lit")
    ap.add_argument("--width", type=int, default=7, help="mask line thickness (px)")
    ap.add_argument("--out-ext", default=None, help="force output format, e.g. tif (default: same as input)")
    ap.add_argument("--stack", action="store_true", help="also write lighten stacks before/after")
    args = ap.parse_args()

    exts = tuple("." + e.lower() for e in args.ext.split(","))
    files = sorted(f for f in glob.glob(os.path.join(args.input_dir, "*")) if f.lower().endswith(exts))
    if len(files) < 3:
        raise SystemExit("need at least 3 frames")
    os.makedirs(args.output_dir, exist_ok=True)

    cache = OrderedDict()  # sliding window of decoded frames

    def get(i):
        if i not in cache:
            cache[i] = load(files[i])
            while len(cache) > 2 * args.k + 2:
                cache.popitem(last=False)
        return cache[i]

    stack_before = stack_after = None
    n = len(files)
    for i in range(n):
        frame = get(i)
        # Neighbours: k on each side, shifted inward at the ends of the sequence.
        lo = max(0, min(i - args.k, n - 1 - 2 * args.k))
        idx = [j for j in range(lo, min(n, lo + 2 * args.k + 1)) if j != i]
        neigh = np.stack([get(j) for j in idx])
        ref_color = np.median(neigh, axis=0)

        mask, segs = detect_trails(to_gray_f32(frame), to_gray_f32(ref_color.astype(frame.dtype)), args)
        clean = repair(frame, ref_color, mask) if segs else frame

        name, ext = os.path.splitext(os.path.basename(files[i]))
        out_ext = "." + args.out_ext if args.out_ext else ext
        params = [cv2.IMWRITE_JPEG_QUALITY, 98] if out_ext.lower() in (".jpg", ".jpeg") else []
        cv2.imwrite(os.path.join(args.output_dir, name + out_ext), clean, params)
        cv2.imwrite(os.path.join(args.output_dir, name + "_mask.png"), mask)
        print(f"[{i + 1}/{n}] {os.path.basename(files[i])}: {len(segs)} trail segment(s)")

        if args.stack:
            stack_before = frame.copy() if stack_before is None else np.maximum(stack_before, frame)
            stack_after = clean.copy() if stack_after is None else np.maximum(stack_after, clean)

    if args.stack:
        cv2.imwrite(os.path.join(args.output_dir, "_stack_before.png"), stack_before)
        cv2.imwrite(os.path.join(args.output_dir, "_stack_after.png"), stack_after)


if __name__ == "__main__":
    main()
