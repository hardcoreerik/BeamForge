using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace BeamForge.App;

public sealed record AudioDevice(string Id, string Name);

public sealed class AudioFeatureAnalyzer
{
    public const int WindowSize = 2048;
    private readonly float[] _previousMagnitudes = new float[WindowSize / 2];
    private readonly Queue<float> _fluxHistory = new();
    private DateTimeOffset _lastBeat = DateTimeOffset.MinValue;
    private float _energyPeak = 0.0001f;
    private float _spectralPeak = 0.0001f;

    public float BeatSensitivity { get; set; } = 1.5f;

    public AudioFeatures Process(ReadOnlySpan<float> samples, int sampleRate, DateTimeOffset timestamp)
    {
        if (samples.Length != WindowSize) throw new ArgumentException($"Audio windows must contain {WindowSize} samples.", nameof(samples));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        Complex[] spectrum = new Complex[WindowSize];
        double squareSum = 0;
        for (int index = 0; index < WindowSize; index++)
        {
            float sample = Math.Clamp(samples[index], -1, 1);
            squareSum += sample * sample;
            spectrum[index].X = sample * (float)FastFourierTransform.HammingWindow(index, WindowSize);
        }
        FastFourierTransform.FFT(true, 11, spectrum);

        float flux = 0;
        float low = 0, mid = 0, high = 0;
        int lowCount = 0, midCount = 0, highCount = 0;
        double binWidth = sampleRate / (double)WindowSize;
        for (int bin = 1; bin < WindowSize / 2; bin++)
        {
            float magnitude = MathF.Sqrt(spectrum[bin].X * spectrum[bin].X + spectrum[bin].Y * spectrum[bin].Y);
            flux += Math.Max(0, magnitude - _previousMagnitudes[bin]);
            _previousMagnitudes[bin] = magnitude;
            double frequency = bin * binWidth;
            if (frequency < 250) { low += magnitude; lowCount++; }
            else if (frequency < 2000) { mid += magnitude; midCount++; }
            else { high += magnitude; highCount++; }
        }

        low /= Math.Max(1, lowCount);
        mid /= Math.Max(1, midCount);
        high /= Math.Max(1, highCount);
        float rms = (float)Math.Sqrt(squareSum / WindowSize);
        float threshold = _fluxHistory.Count == 0 ? float.MaxValue : _fluxHistory.Average() * BeatSensitivity;
        bool beat = flux > threshold && flux > 0.001f && timestamp - _lastBeat >= TimeSpan.FromMilliseconds(250);
        if (beat) _lastBeat = timestamp;
        _fluxHistory.Enqueue(flux);
        while (_fluxHistory.Count > 43) _fluxHistory.Dequeue();

        _spectralPeak = Math.Max(Math.Max(low, Math.Max(mid, high)), _spectralPeak * 0.995f);
        return new AudioFeatures(
            timestamp,
            Normalize(rms, ref _energyPeak),
            beat,
            Math.Clamp(low / _spectralPeak, 0, 1),
            Math.Clamp(mid / _spectralPeak, 0, 1),
            Math.Clamp(high / _spectralPeak, 0, 1));
    }

    private static float Normalize(float value, ref float peak)
    {
        peak = Math.Max(value, peak * 0.995f);
        return Math.Clamp(value / Math.Max(peak, 0.0001f), 0, 1);
    }
}

public sealed class AudioCaptureService : IDisposable
{
    private const int TargetSampleRate = 48000;
    private const int HopSize = AudioFeatureAnalyzer.WindowSize / 2;
    private readonly AudioFeatureAnalyzer _analyzer = new();
    private readonly List<float> _samples = [];
    private readonly object _sync = new();
    private IWaveIn? _capture;
    private System.Threading.Timer? _silenceTimer;
    private DateTimeOffset _lastSignal;
    private bool _silenceRaised;

    public double SilenceTimeoutSeconds { get; set; } = 3;
    public bool IsRunning => _capture is not null;
    public event EventHandler<AudioFeatures>? FeaturesAvailable;
    public event EventHandler? SilenceElapsed;
    public event EventHandler<Exception>? CaptureFailed;

    public static IReadOnlyList<AudioDevice> GetDevices(AudioSourceMode mode)
    {
        using MMDeviceEnumerator enumerator = new();
        DataFlow flow = mode == AudioSourceMode.Microphone ? DataFlow.Capture : DataFlow.Render;
        return enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(device => new AudioDevice(device.ID, device.FriendlyName))
            .ToArray();
    }

    public void Start(AudioSourceMode mode, string? deviceId = null)
    {
        Stop();
        using MMDeviceEnumerator enumerator = new();
        DataFlow flow = mode == AudioSourceMode.Microphone ? DataFlow.Capture : DataFlow.Render;
        MMDevice device = string.IsNullOrWhiteSpace(deviceId)
            ? enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia)
            : enumerator.GetDevice(deviceId);
        _capture = mode == AudioSourceMode.Microphone ? new WasapiCapture(device) : new WasapiLoopbackCapture(device);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        lock (_sync) _samples.Clear();
        _lastSignal = DateTimeOffset.UtcNow;
        _silenceRaised = false;
        _silenceTimer = new System.Threading.Timer(CheckSilence, null, 500, 500);
        _capture.StartRecording();
    }

    public void Stop()
    {
        _silenceTimer?.Dispose();
        _silenceTimer = null;
        if (_capture is null) return;
        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        try { _capture.StopRecording(); }
        catch { /* best effort while the audio device is disappearing */ }
        _capture.Dispose();
        _capture = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (_capture is null) return;
        try
        {
            float[] mono = DecodeMono(args.Buffer.AsSpan(0, args.BytesRecorded), _capture.WaveFormat);
            float[] normalized = Resample(mono, _capture.WaveFormat.SampleRate, TargetSampleRate);
            lock (_sync)
            {
                _samples.AddRange(normalized);
                while (_samples.Count >= AudioFeatureAnalyzer.WindowSize)
                {
                    float[] window = _samples.GetRange(0, AudioFeatureAnalyzer.WindowSize).ToArray();
                    _samples.RemoveRange(0, HopSize);
                    AudioFeatures features = _analyzer.Process(window, TargetSampleRate, DateTimeOffset.UtcNow);
                    if (features.Energy > 0.01f)
                    {
                        _lastSignal = features.Timestamp;
                        _silenceRaised = false;
                    }
                    FeaturesAvailable?.Invoke(this, features);
                }
            }
        }
        catch (Exception exception)
        {
            CaptureFailed?.Invoke(this, exception);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null) CaptureFailed?.Invoke(this, args.Exception);
    }

    private void CheckSilence(object? state)
    {
        if (!_silenceRaised && DateTimeOffset.UtcNow - _lastSignal >= TimeSpan.FromSeconds(SilenceTimeoutSeconds))
        {
            _silenceRaised = true;
            SilenceElapsed?.Invoke(this, EventArgs.Empty);
        }
    }

    internal static float[] DecodeMono(ReadOnlySpan<byte> bytes, WaveFormat format)
    {
        int bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample is < 2 or > 4 || format.Channels < 1) throw new NotSupportedException($"Unsupported audio format: {format}.");
        int frameSize = bytesPerSample * format.Channels;
        float[] result = new float[bytes.Length / frameSize];
        bool floatingPoint = format.Encoding == WaveFormatEncoding.IeeeFloat || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32);
        for (int frame = 0; frame < result.Length; frame++)
        {
            float sum = 0;
            for (int channel = 0; channel < format.Channels; channel++)
            {
                int offset = frame * frameSize + channel * bytesPerSample;
                sum += floatingPoint ? BitConverter.ToSingle(bytes.Slice(offset, 4)) : format.BitsPerSample switch
                {
                    16 => BitConverter.ToInt16(bytes.Slice(offset, 2)) / 32768f,
                    24 => ReadInt24(bytes.Slice(offset, 3)) / 8388608f,
                    32 => BitConverter.ToInt32(bytes.Slice(offset, 4)) / 2147483648f,
                    _ => throw new NotSupportedException($"Unsupported PCM depth: {format.BitsPerSample}.")
                };
            }
            result[frame] = Math.Clamp(sum / format.Channels, -1, 1);
        }
        return result;
    }

    internal static float[] Resample(ReadOnlySpan<float> input, int inputRate, int outputRate)
    {
        if (input.Length == 0 || inputRate == outputRate) return input.ToArray();
        int outputLength = Math.Max(1, (int)Math.Round(input.Length * outputRate / (double)inputRate));
        float[] output = new float[outputLength];
        double step = inputRate / (double)outputRate;
        for (int index = 0; index < output.Length; index++)
        {
            double source = Math.Min(index * step, input.Length - 1);
            int left = (int)source;
            int right = Math.Min(left + 1, input.Length - 1);
            float amount = (float)(source - left);
            output[index] = input[left] + (input[right] - input[left]) * amount;
        }
        return output;
    }

    private static int ReadInt24(ReadOnlySpan<byte> bytes)
    {
        int value = bytes[0] | bytes[1] << 8 | bytes[2] << 16;
        return (value & 0x800000) == 0 ? value : value | unchecked((int)0xFF000000);
    }

    public void Dispose() => Stop();
}
