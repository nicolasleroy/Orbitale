using Orbitale.Engine.Blending;

namespace Orbitale.Engine.Stacking;

/// <summary>
/// Settings of one run. Comet and gap filling only apply to <see cref="BlendMode.Lighten"/> and are ignored otherwise.
/// <see cref="HeadAtStart"/> processes the frames in reverse order (the comet head then sits at the start of the motion).
/// </summary>
public sealed record StackSettings(
    BlendMode Mode = BlendMode.Lighten,
    bool Comet = false,
    double CometStrength = 0.5,
    bool HeadAtStart = false,
    bool GapFill = false,
    double GapFillStrength = 0.5,
    bool RemoveTrails = true,
    double TrailSensitivity = 0.6,
    int NeighbourK = 2,
    int PreviewMaxLongSide = 2560);
