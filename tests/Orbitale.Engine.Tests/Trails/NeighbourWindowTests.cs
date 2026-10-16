using Orbitale.Engine.Frames;

namespace Orbitale.Engine.Tests.Trails;

public class NeighbourWindowTests
{
    [Theory]
    [InlineData(0, 10, 2, new[] { 1, 2, 3, 4 })]
    [InlineData(5, 10, 2, new[] { 3, 4, 6, 7 })]
    [InlineData(9, 10, 2, new[] { 5, 6, 7, 8 })]
    [InlineData(1, 3, 2, new[] { 0, 2 })] // k shrinks to 1
    public void IndicesMatchPrototype(int i, int n, int k, int[] expected) =>
        Assert.Equal(expected, NeighbourWindow.Indices(i, n, k));

    [Theory]
    [InlineData(1, 2, 0)]
    [InlineData(2, 2, 0)]
    [InlineData(3, 2, 1)]
    [InlineData(4, 2, 1)]
    [InlineData(5, 2, 2)]
    public void EffectiveK(int n, int k, int e) => Assert.Equal(e, NeighbourWindow.EffectiveK(n, k));

    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(1, 2, 3)]
    public void TooFewFramesGivesNoNeighbours(int i, int n, int k) =>
        Assert.Empty(NeighbourWindow.Indices(i, n, k));
}
