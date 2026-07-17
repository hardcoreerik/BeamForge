namespace BeamForge.App;

public interface IDmxOutput : IAsyncDisposable
{
    string Name { get; }
    bool IsSimulation { get; }
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task SendAsync(ReadOnlyMemory<byte> universe, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

public sealed class SimulatedDmxOutput : IDmxOutput
{
    public string Name => "In-memory simulator";
    public bool IsSimulation => true;
    public bool IsConnected { get; private set; }
    public byte[] LastFrame { get; private set; } = new byte[512];
    public event EventHandler<byte[]>? FrameSent;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task SendAsync(ReadOnlyMemory<byte> universe, CancellationToken cancellationToken = default)
    {
        if (!IsConnected) throw new InvalidOperationException("DMX output is not connected.");
        if (universe.Length != 512) throw new ArgumentException("A DMX universe must contain 512 slots.", nameof(universe));
        LastFrame = universe.ToArray();
        FrameSent?.Invoke(this, LastFrame);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}

public sealed class SafetyController : IAsyncDisposable
{
    private readonly IDmxOutput _output;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly byte[] _universe = new byte[512];
    private FixtureProfile _profile;
    private int _startAddress;
    private int _intensityCeilingPercent;

    public SafetyController(IDmxOutput output, FixtureProfile profile, int startAddress = 1, int intensityCeilingPercent = 25)
    {
        _output = output;
        _profile = profile;
        SetConfiguration(startAddress, intensityCeilingPercent);
    }

    public bool IsConnected => _output.IsConnected;
    public bool IsArmed { get; private set; }
    public FixtureProfile Profile => _profile;
    public int StartAddress => _startAddress;
    public int IntensityCeilingPercent => _intensityCeilingPercent;
    public byte[] CurrentUniverse => (byte[])_universe.Clone();
    public event EventHandler? StateChanged;

    public void SetProfile(FixtureProfile profile)
    {
        if (IsConnected || IsArmed) throw new InvalidOperationException("Disconnect before changing fixture profiles.");
        _profile = profile;
        Array.Clear(_universe);
        SetConfiguration(_startAddress, _intensityCeilingPercent);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetConfiguration(int startAddress, int intensityCeilingPercent)
    {
        if (IsArmed) throw new InvalidOperationException("Disarm output before changing safety limits.");
        if (startAddress < 1 || startAddress + _profile.ChannelCount - 1 > 512) throw new ArgumentOutOfRangeException(nameof(startAddress));
        if (intensityCeilingPercent is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(intensityCeilingPercent));
        _startAddress = startAddress;
        _intensityCeilingPercent = intensityCeilingPercent;
        Array.Clear(_universe);
        foreach ((int number, byte value) in _profile.BlackoutValues)
            if (number >= 1 && number <= _profile.ChannelCount)
                _universe[_startAddress + number - 2] = value;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _output.ConnectAsync(cancellationToken);
        await BlackoutAsync(cancellationToken);
    }

    public async Task ArmAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected) throw new InvalidOperationException("Connect an output before arming.");
        IReadOnlyList<string> errors = _profile.Validate(forLiveOutput: !_output.IsSimulation);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        IsArmed = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
        await SendCurrentAsync(cancellationToken);
    }

    public Task SetChannelAsync(int channel, byte value, bool fromAutomation = false, CancellationToken cancellationToken = default) =>
        ApplyValuesAsync(new Dictionary<int, byte> { [channel] = value }, fromAutomation, cancellationToken);

    public async Task ApplyValuesAsync(IReadOnlyDictionary<int, byte> values, bool fromAutomation = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsArmed) throw new InvalidOperationException("Output is not armed.");
            foreach ((int number, byte requestedValue) in values)
            {
                ChannelDefinition channel = _profile.Channel(number) ?? throw new ArgumentOutOfRangeException(nameof(values), $"Channel {number} is not defined.");
                if (channel.Hazardous) throw new InvalidOperationException($"Hazardous channel {number} is locked.");
                if (fromAutomation && !channel.Automatable) throw new InvalidOperationException($"Channel {number} is not automatable.");

                byte value = fromAutomation
                    ? (byte)Math.Clamp(requestedValue, channel.AutomationMin, channel.AutomationMax)
                    : requestedValue;
                if (channel.Role == ChannelRole.Intensity)
                    value = (byte)Math.Min(value, 255 * _intensityCeilingPercent / 100);
                _universe[_startAddress + number - 2] = value;
            }
            await _output.SendAsync(_universe, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task ApplySceneAsync(Scene scene, CancellationToken cancellationToken = default) => ApplyValuesAsync(scene.Values, cancellationToken: cancellationToken);

    public async Task BlackoutAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            IsArmed = false;
            Array.Clear(_universe);
            foreach ((int number, byte value) in _profile.BlackoutValues)
                if (number >= 1 && number <= _profile.ChannelCount)
                    _universe[_startAddress + number - 2] = value;
            if (_output.IsConnected) await _output.SendAsync(_universe, cancellationToken);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_output.IsConnected)
        {
            try { await BlackoutAsync(cancellationToken); }
            finally { await _output.DisconnectAsync(cancellationToken); }
        }
        IsArmed = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private Task SendCurrentAsync(CancellationToken cancellationToken) => _output.SendAsync(_universe, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        await _output.DisposeAsync();
        _gate.Dispose();
    }
}

public sealed class AutoShowEngine(SafetyController safetyController)
{
    public async Task ApplyAsync(AudioFeatures features, IEnumerable<AudioMapping> mappings, CancellationToken cancellationToken = default)
    {
        Dictionary<int, byte> values = [];
        foreach (AudioMapping mapping in mappings)
        {
            float amount = Math.Clamp(features.Value(mapping.Feature), 0, 1);
            if (mapping.Invert) amount = 1 - amount;
            values[mapping.Channel] = (byte)Math.Round(mapping.Min + (mapping.Max - mapping.Min) * amount);
        }
        if (values.Count > 0) await safetyController.ApplyValuesAsync(values, fromAutomation: true, cancellationToken);
    }
}
