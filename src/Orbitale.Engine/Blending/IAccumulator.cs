using OpenCvSharp;

namespace Orbitale.Engine.Blending;

public enum BlendMode { Lighten, Darken, Average }

/// <summary>Running blend of CV_32FC3 frames, added one at a time.</summary>
public interface IAccumulator : IDisposable
{
    /// <summary>Adds a frame. Throws <see cref="ArgumentException"/> if its size or type differs from the first frame.</summary>
    void Add(Mat frame32f);

    /// <summary>The running result, owned by the accumulator (clone it to keep it). Throws <see cref="InvalidOperationException"/> before the first Add.</summary>
    Mat Current { get; }

    int Count { get; }
}
