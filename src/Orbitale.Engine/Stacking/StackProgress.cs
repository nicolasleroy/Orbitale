using Orbitale.Engine.Imaging;
using Orbitale.Engine.Trails;

namespace Orbitale.Engine.Stacking;

/// <summary>
/// Reported once per frame, after it was blended (or skipped because it could not be decoded).
/// <see cref="Processed"/> counts frames done in this run (1..<see cref="Total"/>), <see cref="FileIndex"/> is the frame's
/// index in <see cref="Frames.FrameSequence.Files"/>. <see cref="RemovedTrailsPreviewCoords"/> are this frame's removed
/// trails in <see cref="Preview"/> pixel coordinates. <see cref="Notice"/> is a one-off message for the user, or null.
/// <see cref="TrailRemovalSkipped"/> counts the frames so far whose trail removal was skipped because no neighbour's sky
/// could be aligned (see <see cref="StackResult.TrailRemovalSkipped"/>).
/// </summary>
public sealed record StackProgress(
    int FileIndex,
    int Processed,
    int Total,
    int TrailsInFrame,
    int TrailsTotal,
    TimeSpan? Eta,
    PreviewImage Preview,
    IReadOnlyList<LineSegment> RemovedTrailsPreviewCoords,
    string? Notice,
    int TrailRemovalSkipped = 0);
