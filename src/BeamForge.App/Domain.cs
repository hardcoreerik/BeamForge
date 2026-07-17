using System.Text.Json.Serialization;

namespace BeamForge.App;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvidenceStatus
{
    Provisional,
    Verified
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChannelRole
{
    Other,
    Intensity,
    Mode,
    Pattern,
    Color,
    PositionX,
    PositionY,
    Rotation,
    Size,
    Speed,
    Strobe,
    Reset
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioSourceMode
{
    Microphone,
    SystemOutput
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioFeature
{
    Energy,
    Beat,
    Low,
    Mid,
    High
}

public sealed class ChannelRange
{
    public int Min { get; set; }
    public int Max { get; set; } = 255;
    public string Label { get; set; } = string.Empty;
}

public sealed class ChannelDefinition
{
    public int Number { get; set; }
    public string Name { get; set; } = string.Empty;
    public ChannelRole Role { get; set; }
    public byte SafeValue { get; set; }
    public bool Automatable { get; set; }
    public byte AutomationMin { get; set; }
    public byte AutomationMax { get; set; } = 255;
    public bool Hazardous { get; set; }
    public List<ChannelRange> Ranges { get; set; } = [];
}

public sealed class FixtureProfile
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int ChannelCount { get; set; }
    public EvidenceStatus Evidence { get; set; } = EvidenceStatus.Provisional;
    public bool SimulationOnly { get; set; }
    public string EvidenceNote { get; set; } = string.Empty;
    public List<ChannelDefinition> Channels { get; set; } = [];
    public Dictionary<int, byte> BlackoutValues { get; set; } = [];

    public ChannelDefinition? Channel(int number) => Channels.FirstOrDefault(channel => channel.Number == number);

    public IReadOnlyList<string> Validate(bool forLiveOutput = false)
    {
        List<string> errors = [];
        if (SchemaVersion != 1) errors.Add("Unsupported fixture schema version.");
        if (string.IsNullOrWhiteSpace(Id)) errors.Add("Fixture id is required.");
        if (string.IsNullOrWhiteSpace(Manufacturer)) errors.Add("Manufacturer is required.");
        if (string.IsNullOrWhiteSpace(Model)) errors.Add("Model is required.");
        if (ChannelCount is < 1 or > 512) errors.Add("Channel count must be between 1 and 512.");

        foreach (IGrouping<int, ChannelDefinition> duplicate in Channels.GroupBy(channel => channel.Number).Where(group => group.Count() > 1))
            errors.Add($"Channel {duplicate.Key} is defined more than once.");

        foreach (ChannelDefinition channel in Channels)
        {
            if (channel.Number < 1 || channel.Number > ChannelCount)
                errors.Add($"Channel {channel.Number} is outside this fixture's footprint.");
            if (string.IsNullOrWhiteSpace(channel.Name))
                errors.Add($"Channel {channel.Number} needs a name.");
            if (channel.AutomationMin > channel.AutomationMax)
                errors.Add($"Channel {channel.Number} has an invalid automation range.");

            ChannelRange[] ranges = channel.Ranges.OrderBy(range => range.Min).ToArray();
            for (int index = 0; index < ranges.Length; index++)
            {
                ChannelRange range = ranges[index];
                if (range.Min is < 0 or > 255 || range.Max is < 0 or > 255 || range.Min > range.Max)
                    errors.Add($"Channel {channel.Number} has an invalid value range.");
                if (index > 0 && ranges[index - 1].Max >= range.Min)
                    errors.Add($"Channel {channel.Number} has overlapping value ranges.");
            }
        }

        foreach (int number in BlackoutValues.Keys.Where(number => number < 1 || number > ChannelCount))
            errors.Add($"Blackout value references invalid channel {number}.");

        if (forLiveOutput)
        {
            if (SimulationOnly) errors.Add("Simulation-only profiles cannot drive hardware.");
            if (Evidence != EvidenceStatus.Verified) errors.Add("Fixture evidence is not verified.");
            if (Channels.Count != ChannelCount) errors.Add("Every live channel must be defined.");
            foreach (int number in Enumerable.Range(1, Math.Max(ChannelCount, 0)).Where(number => !BlackoutValues.ContainsKey(number)))
                errors.Add($"Channel {number} has no explicit blackout value.");
        }

        return errors.Distinct().ToArray();
    }

    public static FixtureProfile CreateSimulator() => new()
    {
        Id = "beamforge-simulator-rgb",
        Manufacturer = "BeamForge",
        Model = "RGB Laser Simulator",
        ChannelCount = 8,
        Evidence = EvidenceStatus.Verified,
        SimulationOnly = true,
        EvidenceNote = "Synthetic profile for simulator and automated tests only.",
        Channels =
        [
            NewChannel(1, "Intensity", ChannelRole.Intensity, true),
            NewChannel(2, "Pattern", ChannelRole.Pattern, true),
            NewChannel(3, "Color", ChannelRole.Color, true),
            NewChannel(4, "X Position", ChannelRole.PositionX, true),
            NewChannel(5, "Y Position", ChannelRole.PositionY, true),
            NewChannel(6, "Rotation", ChannelRole.Rotation, true),
            NewChannel(7, "Size", ChannelRole.Size, true),
            NewChannel(8, "Speed", ChannelRole.Speed, true)
        ],
        BlackoutValues = Enumerable.Range(1, 8).ToDictionary(number => number, _ => (byte)0)
    };

    private static ChannelDefinition NewChannel(int number, string name, ChannelRole role, bool automatable) => new()
    {
        Number = number,
        Name = name,
        Role = role,
        Automatable = automatable,
        AutomationMin = 0,
        AutomationMax = 255,
        SafeValue = 0
    };
}

public sealed class Scene
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<int, byte> Values { get; set; } = [];
}

public sealed class AudioMapping
{
    public int Channel { get; set; }
    public AudioFeature Feature { get; set; }
    public byte Min { get; set; }
    public byte Max { get; set; } = 255;
    public bool Invert { get; set; }
}

public sealed class ShowProject
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "Untitled Show";
    public string FixtureProfileId { get; set; } = string.Empty;
    public int StartAddress { get; set; } = 1;
    public AudioSourceMode AudioSource { get; set; } = AudioSourceMode.Microphone;
    public double SilenceTimeoutSeconds { get; set; } = 3;
    public int IntensityCeilingPercent { get; set; } = 25;
    public List<Scene> Scenes { get; set; } = [];
    public List<string> Cues { get; set; } = [];
    public List<AudioMapping> AutoMappings { get; set; } = [];

    public IReadOnlyList<string> Validate(FixtureProfile profile)
    {
        List<string> errors = [];
        if (SchemaVersion != 1) errors.Add("Unsupported show schema version.");
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("Show name is required.");
        if (!string.Equals(FixtureProfileId, profile.Id, StringComparison.OrdinalIgnoreCase)) errors.Add("Show fixture does not match the loaded profile.");
        if (StartAddress < 1 || StartAddress + profile.ChannelCount - 1 > 512) errors.Add("Fixture address exceeds the DMX universe.");
        if (SilenceTimeoutSeconds is < 1 or > 30) errors.Add("Silence timeout must be between 1 and 30 seconds.");
        if (IntensityCeilingPercent is < 1 or > 100) errors.Add("Intensity ceiling must be between 1 and 100 percent.");
        foreach (AudioMapping mapping in AutoMappings)
        {
            ChannelDefinition? channel = profile.Channel(mapping.Channel);
            if (channel is null) errors.Add($"Audio mapping references missing channel {mapping.Channel}.");
            else if (!channel.Automatable || channel.Hazardous) errors.Add($"Channel {mapping.Channel} cannot be automated.");
            if (mapping.Min > mapping.Max) errors.Add($"Audio mapping for channel {mapping.Channel} has an invalid range.");
        }
        return errors.Distinct().ToArray();
    }

    public static ShowProject CreateDefault(FixtureProfile profile) => new()
    {
        FixtureProfileId = profile.Id,
        AutoMappings = profile.Channels.Where(channel => channel.Automatable).Select(channel => new AudioMapping
        {
            Channel = channel.Number,
            Feature = channel.Role switch
            {
                ChannelRole.Intensity => AudioFeature.Energy,
                ChannelRole.Speed => AudioFeature.Beat,
                ChannelRole.Color => AudioFeature.High,
                ChannelRole.PositionX or ChannelRole.PositionY => AudioFeature.Mid,
                _ => AudioFeature.Low
            },
            Min = channel.AutomationMin,
            Max = channel.AutomationMax
        }).ToList()
    };
}

public readonly record struct AudioFeatures(DateTimeOffset Timestamp, float Energy, bool Beat, float Low, float Mid, float High)
{
    public float Value(AudioFeature feature) => feature switch
    {
        AudioFeature.Energy => Energy,
        AudioFeature.Beat => Beat ? 1 : 0,
        AudioFeature.Low => Low,
        AudioFeature.Mid => Mid,
        AudioFeature.High => High,
        _ => 0
    };
}
