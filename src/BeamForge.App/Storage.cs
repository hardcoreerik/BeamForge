using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeamForge.App;

public static class JsonStore
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static FixtureProfile LoadFixture(string path)
    {
        FixtureProfile profile = Load<FixtureProfile>(path);
        IReadOnlyList<string> errors = profile.Validate();
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        return profile;
    }

    public static void SaveFixture(string path, FixtureProfile profile)
    {
        IReadOnlyList<string> errors = profile.Validate();
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        Save(path, profile);
    }

    public static ShowProject LoadProject(string path, FixtureProfile profile)
    {
        ShowProject project = Load<ShowProject>(path);
        IReadOnlyList<string> errors = project.Validate(profile);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        return project;
    }

    public static void SaveProject(string path, ShowProject project, FixtureProfile profile)
    {
        IReadOnlyList<string> errors = project.Validate(profile);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        Save(path, project);
    }

    private static T Load<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"{Path.GetFileName(path)} contains no data.");

    private static void Save<T>(string path, T value)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporaryPath = fullPath + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, Options));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
