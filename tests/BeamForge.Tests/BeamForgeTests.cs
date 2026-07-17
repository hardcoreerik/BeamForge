using System.Windows.Media;
using System.Windows.Media.Imaging;
using BeamForge.App;
using NAudio.Wave;
using OpenCvSharp;

namespace BeamForge.Tests;

[TestClass]
public sealed class BeamForgeTests
{
    [TestMethod]
    public void LiveProfileRequiresVerifiedEvidenceAndEveryBlackoutValue()
    {
        FixtureProfile profile = new()
        {
            Id = "test",
            Manufacturer = "Test",
            Model = "Fixture",
            ChannelCount = 1,
            Evidence = EvidenceStatus.Provisional,
            Channels = [new ChannelDefinition { Number = 1, Name = "Dimmer" }]
        };

        IReadOnlyList<string> errors = profile.Validate(forLiveOutput: true);

        CollectionAssert.Contains(errors.ToList(), "Fixture evidence is not verified.");
        CollectionAssert.Contains(errors.ToList(), "Channel 1 has no explicit blackout value.");
    }

    [TestMethod]
    public async Task SafetyControllerStartsDisarmedCapsIntensityAndBlackouts()
    {
        SimulatedDmxOutput output = new();
        await using SafetyController safety = new(output, FixtureProfile.CreateSimulator(), intensityCeilingPercent: 25);
        Assert.IsFalse(safety.IsArmed);
        await safety.ConnectAsync();
        await safety.ArmAsync();
        await safety.SetChannelAsync(1, 255);
        Assert.AreEqual(63, output.LastFrame[0]);
        await safety.BlackoutAsync();
        Assert.IsFalse(safety.IsArmed);
        Assert.AreEqual(0, output.LastFrame[0]);
    }

    [TestMethod]
    public async Task HazardousChannelCannotBeDriven()
    {
        FixtureProfile profile = FixtureProfile.CreateSimulator();
        profile.Channels[0].Hazardous = true;
        SimulatedDmxOutput output = new();
        await using SafetyController safety = new(output, profile);
        await safety.ConnectAsync();
        await safety.ArmAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => safety.SetChannelAsync(1, 1));
    }

    [TestMethod]
    public void ProjectRoundTripsAsValidatedJson()
    {
        FixtureProfile profile = FixtureProfile.CreateSimulator();
        ShowProject project = ShowProject.CreateDefault(profile);
        project.Name = "Test Show";
        project.Scenes.Add(new Scene { Name = "Blue", Values = new Dictionary<int, byte> { [3] = 42 } });
        string path = TemporaryPath(".beamforge.json");
        try
        {
            JsonStore.SaveProject(path, project, profile);
            ShowProject loaded = JsonStore.LoadProject(path, profile);
            Assert.AreEqual("Test Show", loaded.Name);
            Assert.AreEqual(42, loaded.Scenes.Single().Values[3]);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void PcmDecodeAndResamplingAreBounded()
    {
        byte[] pcm = [0, 0, 0xFF, 0x7F, 0, 0x80, 0, 0];
        float[] mono = AudioCaptureService.DecodeMono(pcm, new WaveFormat(48000, 16, 2));
        float[] resampled = AudioCaptureService.Resample(mono, 48000, 24000);
        Assert.HasCount(2, mono);
        Assert.HasCount(1, resampled);
        Assert.IsTrue(mono.All(value => value is >= -1 and <= 1));
    }

    [TestMethod]
    public void AnalyzerSeparatesLowFrequencyEnergyAndDetectsAnOnset()
    {
        AudioFeatureAnalyzer analyzer = new();
        DateTimeOffset time = DateTimeOffset.UtcNow;
        analyzer.Process(new float[AudioFeatureAnalyzer.WindowSize], 48000, time);
        float[] lowTone = Enumerable.Range(0, AudioFeatureAnalyzer.WindowSize)
            .Select(index => 0.7f * MathF.Sin(2 * MathF.PI * 100 * index / 48000))
            .ToArray();
        AudioFeatures features = analyzer.Process(lowTone, 48000, time.AddMilliseconds(300));
        Assert.IsTrue(features.Beat);
        Assert.IsGreaterThan(features.Mid, features.Low);
        Assert.IsGreaterThan(features.High, features.Low);
    }

    [TestMethod]
    public void IldaFormat5RoundTripsCoordinatesColorsAndBlanking()
    {
        LaserFrame frame = new(
        [new LaserPoint(-1, 1, 10, 20, 30, false), new LaserPoint(0.5, -0.5, 0, 0, 0, true)],
        TimeSpan.FromMilliseconds(100));
        using MemoryStream stream = new();
        IldaFile.WriteFormat5(stream, [frame]);
        stream.Position = 0;
        IReadOnlyList<LaserFrame> decoded = IldaFile.ReadFormat5(stream);
        Assert.HasCount(1, decoded);
        Assert.HasCount(2, decoded[0].Points);
        Assert.AreEqual(10, decoded[0].Points[0].Red);
        Assert.AreEqual(20, decoded[0].Points[0].Green);
        Assert.AreEqual(30, decoded[0].Points[0].Blue);
        Assert.IsTrue(decoded[0].Points[1].Blank);
        Assert.AreEqual(-1, decoded[0].Points[0].X, 0.0001);
    }

    [TestMethod]
    public void RasterTraceHonorsPointBudgetAndEndsBlanked()
    {
        string path = TemporaryPath(".png");
        try
        {
            using Mat image = new(128, 128, MatType.CV_8UC3, Scalar.White);
            Cv2.Rectangle(image, new Rect(20, 20, 88, 88), Scalar.Black, 3);
            Cv2.ImWrite(path, image);
            LaserAnimation animation = LaserImageTracer.Trace(path, new TraceOptions { PointBudget = 120 });
            Assert.HasCount(1, animation.Frames);
            Assert.IsLessThanOrEqualTo(120, animation.Frames[0].Points.Count);
            Assert.IsTrue(animation.Frames[0].Points[^1].Blank);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void GifFramesArePreservedAndRateLimited()
    {
        string path = TemporaryPath(".gif");
        Thread thread = new(() => WriteTwoFrameGif(path));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        try
        {
            LaserAnimation animation = LaserImageTracer.Trace(path, new TraceOptions { MaxAnimationFps = 15 });
            Assert.HasCount(2, animation.Frames);
            Assert.IsTrue(animation.Frames.All(frame => frame.Duration >= TimeSpan.FromSeconds(1d / 15)));
        }
        finally { File.Delete(path); }
    }

    private static void WriteTwoFrameGif(string path)
    {
        GifBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(Pixel(0, 0, 0)));
        encoder.Frames.Add(BitmapFrame.Create(Pixel(255, 255, 255)));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static BitmapSource Pixel(byte red, byte green, byte blue) => BitmapSource.Create(
        2, 2, 96, 96, PixelFormats.Bgr24, null,
        new byte[] { blue, green, red, blue, green, red, blue, green, red, blue, green, red }, 6);

    private static string TemporaryPath(string extension) => Path.Combine(Path.GetTempPath(), $"beamforge-{Guid.NewGuid():N}{extension}");
}
