# Orbitale — design

Date: 2026-10-16
Status: draft, awaiting approval

## Intent

Orbitale is a Windows 11 desktop app that turns a night-time sequence of ~300 tripod frames into one star-trail / circumpolar image. Its features are modelled on StarStaX (markus-enzweiler.de/software/starstax), with one thing StarStaX lacks: automatic removal of plane and satellite trails.

**Why it exists:** the user shoots circumpolar sequences in the mountains. Modern skies are full of aircraft and satellite streaks, and cleaning 300 frames by hand isn't practical. A Python prototype (`remove_trails.py`, see "Prior work") showed the streaks can be removed automatically while keeping the stars. Orbitale packages that into a pleasant tool the user runs at home.

## The user's workflow (agreed)

1. Develop the RAW files in Photoshop (exposure etc.) and export the sequence as **JPG or TIFF (8- or 16-bit)**. Orbitale never reads RAW files.
2. Open Orbitale and add the frames (a folder or a multi-selection).
3. Choose settings: blend mode, comet style on/off, gap filling on/off, trail removal on/off, plus each feature's strength or sensitivity slider.
4. Press **Start**. Orbitale processes the frames in order. For each frame it removes trails, then blends the frame into the running result. **The image on screen updates live** so the user watches the trails grow.
5. The user can press **Stop** at any time. Whatever has been built so far is kept and can be saved. Pressing Start again always **restarts from the first frame** (with the current settings). There is no resume. The user stops when they're unhappy with what they see, so a restart is what they want.
6. When the run finishes, the user clicks **Save** and writes the image as **JPG or 16-bit TIFF**.

## Goals

- Windows 11 native app, **WinUI 3** front end.
- **Trail removal** for planes and satellites, keeping the stars.
- **Blend modes:** Lighten (the standard star-trail stack), Darken (removes the stars, giving a clean landscape), Average (lower noise, faint trails).
- **Comet style:** trails taper into a bright head, with a strength slider and a switch for which end of the trail carries the head.
- **Gap filling:** closes the dotted breaks between frames (caused by the pause between exposures) so each star trail is one continuous line. It works on top of Lighten and has its own strength setting.
- **Live progressive preview** during the run, which never freezes the UI.
- **Stop at any time** without losing the partial result.
- **Save** as JPG or 16-bit TIFF, keeping full input resolution and bit depth.

## Non-goals (agreed)

- RAW decoding.
- Dark-frame subtraction (exposure is already handled in Photoshop).
- Frame alignment (fixed tripod is assumed).
- Timelapse video export.
- Saving the cleaned individual frames.
- Other platforms (macOS/Linux), cloud, accounts or telemetry. Orbitale runs fully offline.

## Anti-goals (would be a failure even if it "works")

- Stars erased, chopped or visibly damaged by trail removal. Faint missed satellites are a lesser evil than damaged stars.
- Smudges, seams or blurred patches where trails were removed.
- Landscape getting darker or changing colour because of the comet effect.
- UI freezing during a run, or the Stop button not reacting quickly.
- Running out of memory on 300 large frames.
- Copying StarStaX's name, logo, texts or look. Only the feature set is modelled on it.

## Identity

- **Name:** Orbitale.
- **Icon:** rounded square with a deep night-blue vertical gradient (#0d1330 → #1f2a5c). Inside sits a single bold circular trail that fades in along its length and ends in a white comet head, with a small warm-yellow pole star in the centre. The SVG source is in "Appendix: icon". The builder produces the Windows icon sizes from it.

## Main window (agreed: layout "B · image first")

From top to bottom:

1. **Toolbar:** Add frames…, blend mode picker, comet style toggle and strength, gap filling toggle and strength, trail removal toggle and sensitivity, **Start**, **Save…**. The builder may move secondary sliders into a flyout or an expander on the toolbar so it stays uncluttered.
2. **Live image**, filling most of the window. It supports zoom-to-fit and zoom to 100 % with panning, so the user can inspect stars and removed trails. A small overlay badge shows "Live · frame 141 / 300". When a frame has trails removed, those trails are briefly outlined on the live image (e.g. a translucent highlight that fades after about a second) so the user can see and trust what was removed.
3. **Thumbnail strip** of the frames, coloured by state: pending, current, done, done with trails removed (with a count). Clicking a thumbnail opens that original frame in the image view while idle.
4. **Status bar:** progress bar, "141 / 300 · 37 trails removed · ~1 min left", and the **Stop** button while running.

Dark theme by default, consistent with night photography.

## Image processing

### Input

- JPG, TIFF 8-bit and TIFF 16-bit, RGB.
- All frames must share the same dimensions. If one doesn't, show a clear message naming the file, and skip it or abort (the builder decides which).
- Frames are processed in **filename order**. The user can remove frames from the list before starting.
- Expected size: up to ~300 frames of up to ~45 MP.

### Working precision

Processing runs in 32-bit float per channel. Results are written at the output's bit depth (8-bit for JPG, 16-bit for TIFF).

### Trail removal (proven in the prototype; port it)

For each frame *i*:

1. **Temporal reference:** take the per-pixel median of its neighbours *i−k … i+k* (default *k* = 2, so 4 neighbours, frame *i* excluded). Near the start and end of the sequence, shift the window inward. Transient trails vanish from this median.
2. **Star-tolerant residual:** dilate the reference with a ~7 px disc, so stars that drifted a few pixels between frames are covered. Then `residual = frame − dilated_reference`, slightly blurred.
3. **Threshold:** binarise at `median + σ·MAD` of the residual (default σ = 2.5). Drop connected specks smaller than ~4 px.
4. **Line detection:** run a probabilistic Hough transform with a minimum length of ~3 % of the image's long side and a gap tolerance of ~25 px, so the dashed streaks of blinking aircraft lights still count as one line. Keep a segment only if ≥ 35 % of the pixels along it are lit.
5. **Repair:** draw the kept segments into a mask (~7 px thick, dilated a little more), feather it, and blend the frame towards the **un-dilated neighbour median** inside the mask. That median is the real sky at nearly the same moment, so nothing is smeared, and a false detection costs almost nothing.

The **Sensitivity** slider maps mainly to σ. Higher sensitivity means a lower σ and fainter trails caught. Defaults must match the values above. The Python prototype is the reference implementation and its parameters are documented in the script.

Known limit (accepted): a plane that stays in nearly the same place across several consecutive frames will not be removed.

### Blending

The result is a running accumulator, updated after each frame (after trail removal):

- **Lighten:** per-pixel maximum.
- **Darken:** per-pixel minimum.
- **Average:** running mean.

### Comet style

Trails taper so each star looks like a comet with a bright head. The intended behaviour is that older contributions fade as new frames arrive. **The fade must apply only to what the stars add above the sky background, never to the landscape or sky brightness itself** (see anti-goals). One way to do this: keep a background estimate (e.g. a running Darken or median) and fade only the excess of the Lighten accumulator above it. The **strength** slider controls how fast the fade happens. The **head at start / head at end** switch picks the processing direction (reverse order puts the head at the beginning of the night's motion). The exact formula is the builder's call, judged visually on the sample frames.

### Gap filling

Between two exposures the stars jump a little, which leaves short gaps that turn each Lighten trail into a dotted line. Gap filling should close those gaps so trails read as continuous arcs, without thickening them noticeably or bleeding into the landscape. It applies to Lighten (and to Lighten + comet). The **strength** setting controls how large a gap it bridges. The algorithm is the builder's call. Reasonable options are directional morphological closing along the local trail direction, or synthesising intermediate positions between consecutive frames. Whatever is chosen must keep the stacking live (the live view still updates per frame).

### Live preview and responsiveness

- All processing runs off the UI thread. The UI stays responsive at all times.
- After each frame, a display-size rendering of the accumulator (with the trail-outline overlay, if any) is pushed to the image view. If rendering is a bottleneck, refreshes may be throttled (e.g. at most ~10 per second), but every frame must still be visibly reflected within about a second.
- **Stop** reacts within about a second. It cancels the run, keeps the partial accumulator, enables Save and leaves the frame list intact for a restart.
- Memory: keep only a sliding window of decoded frames (2*k* + 2) plus the accumulators in memory, never the whole sequence. Peak memory should stay under ~8 GB for 45 MP frames. To get there, the window can be stored at 16 bits per channel and converted to float per tile or strip when processed (builder's call).
- **Target PC (measured on the user's machine):** 32 GB RAM, Intel i7-11700K (8 cores / 16 threads), NVIDIA RTX 2060, .NET 10 SDK installed. CPU-only processing is expected to be enough. GPU acceleration is not required.
- Performance target: 300 × 24 MP frames processed in a few minutes on that PC. Use all CPU cores where sensible.

### Output

- **Save…** offers JPG (quality ~95) and 16-bit TIFF, at full input resolution.
- Saving is possible after a finished run and after a stopped one.
- **Colour profile and metadata (my call, assistant):** Photoshop exports may be Adobe RGB or ProPhoto. The output must carry the **ICC profile of the first input frame**, so colours don't shift when the image goes back into Photoshop, along with that frame's basic EXIF (camera, lens, date). OpenCV discards both. So file reading and writing goes through a library that keeps profiles (e.g. Windows Imaging Component or Magick.NET). OpenCV is used only for processing.

### Settings memory

The app remembers the last-used settings and output folder between launches.

## Technology

- **Decided by the user:** Windows 11, WinUI 3.
- **My call (assistant):** C# on .NET 10 with the Windows App SDK, MVVM via CommunityToolkit.Mvvm, and **OpenCvSharp** (OpenCV for .NET) for the image operations (median, dilation, Hough lines), the same as the prototype, so the algorithm ports directly. File reading and writing goes through a profile-preserving library (see Output). The processing engine lives in its own class library with no UI dependency, so it can be unit-tested and driven from a small console harness.
- **My call:** distributed as an unpackaged, self-contained app the user runs locally. No Microsoft Store.

## Testing and acceptance

- The three sample frames (`samples/1.jpg … 3.jpg`, copied from the session that produced the prototype) are the reference test data. On them, with default settings and Lighten:
  - the bright red aircraft trail and the main horizontal satellite streaks are gone from the result;
  - the stars in those areas are still present;
  - no visible patching artefacts appear at 100 % zoom.
- **Full real sequence (provided by the user):** `D:\JPG_star_trail`. It holds 279 JPGs (`DSCF6181.jpg … DSCF6459.jpg`, 4.3 GB) from a Fujifilm X-T5 at 7728 × 5152 (40 MP), each with an embedded ICC profile. It's the reference for judging comet style, gap filling, speed, memory and the live preview, and for an end-to-end run with default trail removal (*k* = 2). It sits outside the repo and is never copied into it or modified. Automated tests that need many frames may use a short slice of it (e.g. 20 frames) and skip themselves when the folder is missing.
- Unit tests cover the blend modes, comet fading (landscape brightness unchanged), the neighbour-window logic at sequence ends, and trail detection on the samples.
- Manual check in the running app: live preview updates, Stop keeps the partial result, Save writes both formats, and the app stays responsive during a run.

## Left to the builder

- Exact toolbar arrangement and control styling within layout B.
- The comet fade formula and the gap-filling algorithm, within the constraints above.
- Skip-vs-abort for mismatched frame sizes.
- Preview throttling details and how the trail outline is drawn.
- Project and solution structure.

## Prior work

- `remove_trails.py` (repo root): the Python + OpenCV prototype of the trail removal, validated on the three sample frames. It's the reference for the algorithm and every default parameter (blur kernel, dilation sizes, feathering). Port it, don't re-derive it. With only 3 frames it was validated using `--k 1` (2 neighbours). The acceptance test on the samples uses that too. The Sensitivity slider maps only to σ.
- Mockups from the brainstorming session (layout options, name and icon) are under `.superpowers/brainstorm/`.

## Appendix: icon (SVG source)

```svg
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">
  <defs>
    <linearGradient id="night" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#0d1330"/><stop offset="1" stop-color="#1f2a5c"/>
    </linearGradient>
    <linearGradient id="arc" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#7fb3ff" stop-opacity="0"/><stop offset="1" stop-color="#e9f1ff"/>
    </linearGradient>
  </defs>
  <rect width="64" height="64" rx="14" fill="url(#night)"/>
  <circle cx="32" cy="32" r="19" fill="none" stroke="#2f3c78" stroke-width="5"/>
  <path d="M32 13 a19 19 0 1 1 -18.4 14.3" fill="none" stroke="url(#arc)" stroke-width="5" stroke-linecap="round"/>
  <circle cx="13.6" cy="27.3" r="4.2" fill="#fff"/>
  <circle cx="32" cy="32" r="2.2" fill="#ffd27a"/>
</svg>
```
