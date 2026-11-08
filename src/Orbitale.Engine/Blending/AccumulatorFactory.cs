namespace Orbitale.Engine.Blending;

public static class AccumulatorFactory
{
    /// <summary>Creates an accumulator. The comet style only applies to Lighten; it is ignored for other modes.</summary>
    public static IAccumulator Create(BlendMode mode, bool comet, double fadeIn, double fadeOut, int totalFrames) => mode switch
    {
        BlendMode.Lighten => comet ? new CometAccumulator(totalFrames, fadeIn, fadeOut) : new LightenAccumulator(),
        BlendMode.Darken => new DarkenAccumulator(),
        BlendMode.Average => new AverageAccumulator(),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}
