using OpenCvSharp;

namespace Orbitale.Engine.Trails;

/// <summary>Per-pixel, per-channel temporal median of 2..6 CV_32FC3 frames.</summary>
public static class TemporalMedian
{
    public const int MinFrames = 2;
    public const int MaxFrames = 6;

    public static unsafe Mat Compute(IReadOnlyList<Mat> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        int n = frames.Count;
        if (n < MinFrames || n > MaxFrames)
            throw new ArgumentException($"Temporal median needs {MinFrames}..{MaxFrames} frames, got {n}.", nameof(frames));
        int rows = frames[0].Rows, cols = frames[0].Cols;
        for (int f = 0; f < n; f++)
        {
            var m = frames[f];
            if (m.Type() != MatType.CV_32FC3)
                throw new ArgumentException($"Frame {f} is {m.Type()}, expected CV_32FC3.", nameof(frames));
            if (m.Rows != rows || m.Cols != cols)
                throw new ArgumentException($"Frame {f} is {m.Cols}x{m.Rows}, expected {cols}x{rows}.", nameof(frames));
        }

        var result = new Mat(rows, cols, MatType.CV_32FC3);
        try
        {
            // Mat.Ptr(row) is the per-row base address, so ROI (non-continuous) inputs work.
            int len = cols * 3;
            Parallel.For(0, rows, r =>
            {
                float** src = stackalloc float*[MaxFrames];
                for (int f = 0; f < n; f++) src[f] = (float*)frames[f].Ptr(r);
                float* dst = (float*)result.Ptr(r);
                MedianRow(src, n, dst, len);
            });
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    static unsafe void MedianRow(float** s, int n, float* d, int len)
    {
        switch (n)
        {
            case 2:
                for (int i = 0; i < len; i++) d[i] = (s[0][i] + s[1][i]) * 0.5f;
                break;
            case 3:
                for (int i = 0; i < len; i++)
                {
                    float a = s[0][i], b = s[1][i], c = s[2][i];
                    d[i] = MathF.Max(MathF.Min(a, b), MathF.Min(MathF.Max(a, b), c));
                }
                break;
            case 4:
                for (int i = 0; i < len; i++)
                {
                    float a = s[0][i], b = s[1][i], c = s[2][i], e = s[3][i];
                    float lo1 = MathF.Min(a, b), hi1 = MathF.Max(a, b);
                    float lo2 = MathF.Min(c, e), hi2 = MathF.Max(c, e);
                    d[i] = (MathF.Max(lo1, lo2) + MathF.Min(hi1, hi2)) * 0.5f;
                }
                break;
            default: // 5 or 6: small insertion sort
            {
                float* buf = stackalloc float[MaxFrames];
                for (int i = 0; i < len; i++)
                {
                    for (int f = 0; f < n; f++)
                    {
                        float v = s[f][i];
                        int j = f - 1;
                        while (j >= 0 && buf[j] > v) { buf[j + 1] = buf[j]; j--; }
                        buf[j + 1] = v;
                    }
                    d[i] = (n & 1) == 1 ? buf[n / 2] : (buf[n / 2 - 1] + buf[n / 2]) * 0.5f;
                }
                break;
            }
        }
    }
}
