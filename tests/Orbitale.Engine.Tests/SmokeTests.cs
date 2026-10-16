namespace Orbitale.Engine.Tests;

public class SmokeTests
{
    [Fact] public void OpenCvLoads() => Assert.False(string.IsNullOrEmpty(OpenCvSharp.Cv2.GetVersionString()));
    [Fact] public void MagickLoads() => Assert.False(string.IsNullOrEmpty(ImageMagick.MagickNET.Version));
    [Fact] public void SamplesAreCopied() => Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "samples", "2.jpg")));
}
