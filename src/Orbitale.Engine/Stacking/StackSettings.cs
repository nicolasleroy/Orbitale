using Orbitale.Engine.Blending;

namespace Orbitale.Engine.Stacking;

/// <summary>
/// Settings of one run. Comet and gap filling only apply to <see cref="BlendMode.Lighten"/> and are ignored otherwise.
/// The comet fades its trails in and out along the sequence: <see cref="CometFadeIn"/> is where the fade-in ends and
/// <see cref="CometFadeOut"/> where the fade-out starts, both as fractions of the sequence (0 = first frame, 1 = last),
/// with <c>CometFadeIn ≤ CometFadeOut</c>. Between them the trails keep their full brightness (see <see cref="CometFade"/>).
/// </summary>
public sealed record StackSettings(
    BlendMode Mode = BlendMode.Lighten,
    bool Comet = false,
    double CometFadeIn = 0.5,
    double CometFadeOut = 1.0,
    bool GapFill = false,
    double GapFillStrength = 0.5,
    bool RemoveTrails = true,
    double TrailSensitivity = 0.6,
    int NeighbourK = 2,
    int PreviewMaxLongSide = 2560);
