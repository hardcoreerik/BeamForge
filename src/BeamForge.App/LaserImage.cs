using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace BeamForge.App;

public sealed class TraceOptions
{
    public bool PhotoEdges { get; set; }
    public int Threshold { get; set; } = 128;
    public int EdgeLow { get; set; } = 60;
    public int EdgeHigh { get; set; } = 150;
    public int PointBudget { get; set; } = 600;
    public int BlankingDwell { get; set; } = 4;
    public int CornerDwell { get; set; }
    public double Scale { get; set; } = 0.9;
    public int MaxAnimationFps { get; set; } = 15;

    public void Validate()
    {
        if (Threshold is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(Threshold));
        if (EdgeLow is < 0 or > 255 || EdgeHigh is < 0 or > 255 || EdgeLow >= EdgeHigh) throw new ArgumentOutOfRangeException(nameof(EdgeLow));
        if (PointBudget is < 50 or > 5000) throw new ArgumentOutOfRangeException(nameof(PointBudget));
        if (BlankingDwell is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(BlankingDwell));
        if (CornerDwell is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(CornerDwell));
        if (Scale is < 0.1 or > 1) throw new ArgumentOutOfRangeException(nameof(Scale));
        if (MaxAnimationFps is < 1 or > 15) throw new ArgumentOutOfRangeException(nameof(MaxAnimationFps));
    }
}

public sealed class LaserAnimation(IReadOnlyList<LaserFrame> frames)
{
    public IReadOnlyList<LaserFrame> Frames { get; } = frames;
    public int TotalPoints => Frames.Sum(frame => frame.Points.Count);
}

public static class LaserImageTracer
{
    public static LaserAnimation Trace(string path, TraceOptions options)
    {
        options.Validate();
        BitmapDecoder decoder = BitmapDecoder.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count is < 1 or > 300) throw new InvalidDataException("Images must contain between 1 and 300 frames.");

        List<LaserFrame> frames = [];
        TimeSpan minimumDuration = TimeSpan.FromSeconds(1d / options.MaxAnimationFps);
        foreach (BitmapFrame bitmap in decoder.Frames)
        {
            if (bitmap.PixelWidth is < 1 or > 4096 || bitmap.PixelHeight is < 1 or > 4096)
                throw new InvalidDataException("Image dimensions must be between 1 and 4096 pixels.");
            TimeSpan duration = GetDuration(bitmap);
            if (duration < minimumDuration) duration = minimumDuration;
            frames.Add(new LaserFrame(TraceFrame(bitmap, options), duration));
        }
        return new LaserAnimation(frames);
    }

    private static IReadOnlyList<LaserPoint> TraceFrame(BitmapSource bitmap, TraceOptions options)
    {
        BitmapSource scaled = ScaleForProcessing(bitmap);
        FormatConvertedBitmap converted = new(scaled, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        byte[] pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        CompositeTransparencyOnWhite(pixels);

        using Mat bgra = new(converted.PixelHeight, converted.PixelWidth, MatType.CV_8UC4);
        Marshal.Copy(pixels, 0, bgra.Data, pixels.Length);
        using Mat gray = new();
        using Mat edges = new();
        Cv2.CvtColor(bgra, gray, ColorConversionCodes.BGRA2GRAY);
        if (options.PhotoEdges) Cv2.Canny(gray, edges, options.EdgeLow, options.EdgeHigh);
        else Cv2.Threshold(gray, edges, options.Threshold, 255, ThresholdTypes.BinaryInv);

        Cv2.FindContours(edges, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
        List<List<LaserPoint>> paths = contours
            .Where(contour => contour.Length >= 4)
            .Select(contour => Cv2.ApproxPolyDP(contour, 1.5, false))
            .Where(contour => contour.Length >= 2)
            .Select(contour => contour.Select(point => Normalize(point, converted.PixelWidth, converted.PixelHeight, options.Scale)).ToList())
            .ToList();
        return BuildPointStream(OrderPaths(paths), options);
    }

    private static BitmapSource ScaleForProcessing(BitmapSource source)
    {
        const int maximum = 1024;
        if (Math.Max(source.PixelWidth, source.PixelHeight) <= maximum) return source;
        double factor = maximum / (double)Math.Max(source.PixelWidth, source.PixelHeight);
        return new TransformedBitmap(source, new ScaleTransform(factor, factor));
    }

    private static void CompositeTransparencyOnWhite(Span<byte> pixels)
    {
        for (int index = 0; index < pixels.Length; index += 4)
        {
            int alpha = pixels[index + 3];
            for (int color = 0; color < 3; color++) pixels[index + color] = (byte)((pixels[index + color] * alpha + 255 * (255 - alpha)) / 255);
            pixels[index + 3] = 255;
        }
    }

    private static LaserPoint Normalize(Point point, int width, int height, double scale)
    {
        double size = Math.Max(width, height);
        double x = ((point.X - width / 2d) / (size / 2d)) * scale;
        double y = ((height / 2d - point.Y) / (size / 2d)) * scale;
        return new LaserPoint(Math.Clamp(x, -1, 1), Math.Clamp(y, -1, 1), 255, 255, 255, false);
    }

    private static List<List<LaserPoint>> OrderPaths(List<List<LaserPoint>> paths)
    {
        List<List<LaserPoint>> ordered = [];
        LaserPoint current = new(0, 0, 0, 0, 0, true);
        // ponytail: O(n^2) is bounded by the 300-frame/600-point limits; use a spatial index only if profiling proves it matters.
        while (paths.Count > 0)
        {
            int bestIndex = 0;
            bool reverse = false;
            double bestDistance = double.MaxValue;
            for (int index = 0; index < paths.Count; index++)
            {
                double start = DistanceSquared(current, paths[index][0]);
                double end = DistanceSquared(current, paths[index][^1]);
                if (Math.Min(start, end) >= bestDistance) continue;
                bestDistance = Math.Min(start, end);
                bestIndex = index;
                reverse = end < start;
            }
            List<LaserPoint> selected = paths[bestIndex];
            paths.RemoveAt(bestIndex);
            if (reverse) selected.Reverse();
            ordered.Add(selected);
            current = selected[^1];
        }
        return ordered;
    }

    private static IReadOnlyList<LaserPoint> BuildPointStream(List<List<LaserPoint>> paths, TraceOptions options)
    {
        int maximumPaths = Math.Max(1, options.PointBudget / (options.BlankingDwell * 2 + 2));
        paths = paths.OrderByDescending(path => path.Count).Take(maximumPaths).ToList();
        int reserved = paths.Count * options.BlankingDwell * 2;
        int visibleAllowance = Math.Max(1, (options.PointBudget - reserved) / (options.CornerDwell + 1));
        int visibleCount = paths.Sum(path => path.Count);
        int step = Math.Max(1, (int)Math.Ceiling(visibleCount / (double)visibleAllowance));
        List<LaserPoint> points = new(options.PointBudget);
        foreach (List<LaserPoint> path in paths)
        {
            List<LaserPoint> sampled = path.Where((_, index) => index % step == 0).ToList();
            if (sampled.Count == 0 || sampled[^1] != path[^1]) sampled.Add(path[^1]);
            LaserPoint start = sampled[0] with { Blank = true, Red = 0, Green = 0, Blue = 0 };
            LaserPoint end = sampled[^1] with { Blank = true, Red = 0, Green = 0, Blue = 0 };
            for (int index = 0; index < options.BlankingDwell; index++) points.Add(start);
            foreach (LaserPoint point in sampled)
                for (int index = 0; index <= options.CornerDwell; index++) points.Add(point);
            for (int index = 0; index < options.BlankingDwell; index++) points.Add(end);
            if (points.Count >= options.PointBudget) break;
        }
        if (points.Count > options.PointBudget) points.RemoveRange(options.PointBudget, points.Count - options.PointBudget);
        if (points.Count == 0) points.Add(new LaserPoint(0, 0, 0, 0, 0, true));
        else points[^1] = points[^1] with { Blank = true, Red = 0, Green = 0, Blue = 0 };
        return points;
    }

    private static double DistanceSquared(LaserPoint left, LaserPoint right) => Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2);

    private static TimeSpan GetDuration(BitmapFrame frame)
    {
        if (frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("/grctlext/Delay") is ushort delay && delay > 0)
            return TimeSpan.FromMilliseconds(delay * 10);
        return TimeSpan.FromMilliseconds(100);
    }
}
