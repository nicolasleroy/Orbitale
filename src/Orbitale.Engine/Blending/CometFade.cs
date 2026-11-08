namespace Orbitale.Engine.Blending;

/// <summary>
/// The comet's brightness envelope along the sequence. A frame at position <c>t</c> (0 = first frame, 1 = last) keeps
/// the fraction <c>w(t)</c> of its star light: it rises linearly from 0 to 1 over <c>[0, fadeIn]</c>, stays at 1 over
/// <c>[fadeIn, fadeOut]</c> and falls linearly back to 0 over <c>[fadeOut, 1]</c>. A fade-in of 0 or a fade-out of 1
/// leaves that end at full brightness. The classic comet (bright head at the end) is <c>fadeIn = 1, fadeOut = 1</c>; with
/// the head at the start it is <c>0, 0</c>.
/// </summary>
public static class CometFade
{
    /// <summary>Both bounds clamped to 0..1 (non-finite ones take the defaults) and ordered so fade-in ≤ fade-out.</summary>
    public static (double FadeIn, double FadeOut) Normalize(double fadeIn, double fadeOut)
    {
        double a = double.IsFinite(fadeIn) ? Math.Clamp(fadeIn, 0, 1) : 0.5;
        double b = double.IsFinite(fadeOut) ? Math.Clamp(fadeOut, 0, 1) : 1.0;
        return a <= b ? (a, b) : (b, a);
    }

    /// <summary>The weight of frame <paramref name="index"/> of <paramref name="total"/> (a single frame keeps all its light).</summary>
    public static float Weight(int index, int total, double fadeIn, double fadeOut)
    {
        if (total <= 1) return 1;
        (fadeIn, fadeOut) = Normalize(fadeIn, fadeOut);
        double t = Math.Clamp((double)index / (total - 1), 0, 1);
        double rise = t >= fadeIn ? 1 : t / fadeIn;                 // fadeIn > 0 here
        double fall = t <= fadeOut ? 1 : (1 - t) / (1 - fadeOut);  // fadeOut < 1 here
        return (float)Math.Min(rise, fall);
    }
}
