using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace BeamForge.App;

public readonly record struct LaserPoint(double X, double Y, byte Red, byte Green, byte Blue, bool Blank);

public sealed record LaserFrame(IReadOnlyList<LaserPoint> Points, TimeSpan Duration);

public sealed record IldaSectionInfo(byte FormatCode, string Name, string Company, ushort RecordCount, ushort FrameNumber, ushort TotalFrames);

public static class IldaFile
{
    private const int HeaderSize = 32;

    public static void ExportFormat5(string destinationPath, IReadOnlyList<LaserFrame> frames, string name = "BEAMFRG", string company = "BEAMFRG")
    {
        if (File.Exists(destinationPath)) throw new IOException("BeamForge will not overwrite an existing ILDA file.");
        string fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporaryPath = fullPath + ".tmp";
        try
        {
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                WriteFormat5(stream, frames, name, company);
            File.Move(temporaryPath, fullPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static void WriteFormat5(Stream destination, IReadOnlyList<LaserFrame> frames, string name = "BEAMFRG", string company = "BEAMFRG")
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("Destination must be writable.", nameof(destination));
        if (frames.Count is < 1 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(frames));

        Span<byte> record = stackalloc byte[8];
        for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
        {
            IReadOnlyList<LaserPoint> points = frames[frameIndex].Points;
            if (points.Count is < 1 or > ushort.MaxValue) throw new InvalidDataException("Each ILDA frame must contain 1 to 65535 points.");
            WriteHeader(destination, 5, name, company, (ushort)points.Count, (ushort)frameIndex, (ushort)frames.Count);
            for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
            {
                LaserPoint point = points[pointIndex];
                BinaryPrimitives.WriteInt16BigEndian(record, ToCoordinate(point.X));
                BinaryPrimitives.WriteInt16BigEndian(record[2..], ToCoordinate(point.Y));
                record[4] = (byte)((pointIndex == points.Count - 1 ? 0x80 : 0) | (point.Blank ? 0x40 : 0));
                record[5] = point.Blue;
                record[6] = point.Green;
                record[7] = point.Red;
                destination.Write(record);
            }
        }

        WriteHeader(destination, 0, string.Empty, string.Empty, 0, 0, 0);
    }

    public static IReadOnlyList<IldaSectionInfo> Inspect(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("Source must be readable.", nameof(source));
        List<IldaSectionInfo> sections = [];
        Span<byte> header = stackalloc byte[HeaderSize];
        while (ReadExactlyOrEnd(source, header))
        {
            ValidateHeader(header);
            byte format = header[7];
            ushort count = BinaryPrimitives.ReadUInt16BigEndian(header[24..26]);
            if (count == 0) break;
            int recordSize = format switch { 0 => 8, 1 => 6, 2 => 3, 4 => 10, 5 => 8, _ => throw new InvalidDataException($"Unsupported ILDA format {format}.") };
            sections.Add(new IldaSectionInfo(
                format,
                ReadAscii(header[8..16]),
                ReadAscii(header[16..24]),
                count,
                BinaryPrimitives.ReadUInt16BigEndian(header[26..28]),
                BinaryPrimitives.ReadUInt16BigEndian(header[28..30])));
            SkipExactly(source, checked(count * recordSize));
        }
        return sections;
    }

    public static IReadOnlyList<LaserFrame> ReadFormat5(Stream source)
    {
        List<LaserFrame> frames = [];
        Span<byte> header = stackalloc byte[HeaderSize];
        Span<byte> record = stackalloc byte[8];
        while (ReadExactlyOrEnd(source, header))
        {
            ValidateHeader(header);
            byte format = header[7];
            ushort count = BinaryPrimitives.ReadUInt16BigEndian(header[24..26]);
            if (count == 0) break;
            if (format != 5) throw new InvalidDataException($"Expected ILDA format 5 but found format {format}.");

            List<LaserPoint> points = new(count);
            for (int index = 0; index < count; index++)
            {
                ReadExactly(source, record);
                short x = BinaryPrimitives.ReadInt16BigEndian(record);
                short y = BinaryPrimitives.ReadInt16BigEndian(record[2..]);
                points.Add(new LaserPoint(FromCoordinate(x), FromCoordinate(y), record[7], record[6], record[5], (record[4] & 0x40) != 0));
            }
            frames.Add(new LaserFrame(points, TimeSpan.Zero));
        }
        return frames;
    }

    private static void WriteHeader(Stream destination, byte format, string name, string company, ushort records, ushort frame, ushort totalFrames)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        Encoding.ASCII.GetBytes("ILDA", header);
        header[7] = format;
        WriteAscii(name, header[8..16]);
        WriteAscii(company, header[16..24]);
        BinaryPrimitives.WriteUInt16BigEndian(header[24..26], records);
        BinaryPrimitives.WriteUInt16BigEndian(header[26..28], frame);
        BinaryPrimitives.WriteUInt16BigEndian(header[28..30], totalFrames);
        destination.Write(header);
    }

    private static short ToCoordinate(double value)
    {
        value = Math.Clamp(value, -1, 1);
        return value <= -1 ? short.MinValue : (short)Math.Round(value * short.MaxValue);
    }

    private static double FromCoordinate(short value) => value < 0 ? value / 32768d : value / 32767d;

    private static void ValidateHeader(ReadOnlySpan<byte> header)
    {
        if (!header[..4].SequenceEqual("ILDA"u8)) throw new InvalidDataException("Invalid ILDA header.");
    }

    private static void WriteAscii(string value, Span<byte> destination)
    {
        destination.Clear();
        Encoding.ASCII.GetBytes(value.AsSpan(0, Math.Min(value.Length, destination.Length)), destination);
    }

    private static string ReadAscii(ReadOnlySpan<byte> value)
    {
        int zero = value.IndexOf((byte)0);
        return Encoding.ASCII.GetString(zero >= 0 ? value[..zero] : value).TrimEnd();
    }

    private static bool ReadExactlyOrEnd(Stream source, Span<byte> buffer)
    {
        int first = source.Read(buffer);
        if (first == 0) return false;
        int read = first;
        while (read < buffer.Length)
        {
            int amount = source.Read(buffer[read..]);
            if (amount == 0) throw new EndOfStreamException();
            read += amount;
        }
        return true;
    }

    private static void ReadExactly(Stream source, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int amount = source.Read(buffer[read..]);
            if (amount == 0) throw new EndOfStreamException();
            read += amount;
        }
    }

    private static void SkipExactly(Stream source, int bytes)
    {
        Span<byte> buffer = stackalloc byte[1024];
        while (bytes > 0)
        {
            int read = source.Read(buffer[..Math.Min(bytes, buffer.Length)]);
            if (read == 0) throw new EndOfStreamException();
            bytes -= read;
        }
    }
}
