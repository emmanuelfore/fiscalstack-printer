using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrintInterceptor.Core.Configuration;

/// <summary>
/// Where parse profiles live and how they move between disk, Settings and a till.
/// Built-in profiles ship read-only inside the application folder; imported and saved
/// ones sit next to the configuration file, so a profile written for one till can be
/// copied to another without rebuilding anything.
/// </summary>
public sealed class ParseProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // A profile is a hand-edited file of regular expressions. The default encoder
        // escapes quotes, angle brackets and plus signs, which turns a pattern into
        // unreadable \u sequences; these files never end up inside HTML.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly List<string> _loadErrors = new();

    public ParseProfileStore(string builtInDirectory, string userDirectory)
    {
        BuiltInDirectory = builtInDirectory;
        UserDirectory = userDirectory;
    }

    /// <summary>The directories a running till uses: bundled profiles, then the config folder.</summary>
    public static ParseProfileStore ForCurrentInstall()
    {
        var configDirectory = Path.GetDirectoryName(InterceptorConfig.DefaultConfigPath())
                              ?? throw new InvalidOperationException("No configuration directory to place profiles in.");

        return new ParseProfileStore(
            Path.Combine(AppContext.BaseDirectory, "profiles"),
            Path.Combine(configDirectory, "profiles"));
    }

    /// <summary>Profiles that came with the application. Read-only.</summary>
    public string BuiltInDirectory { get; }

    /// <summary>Imported and saved profiles. Created on first write.</summary>
    public string UserDirectory { get; }

    /// <summary>
    /// Files that could not be read during the last <see cref="List"/> call, so the
    /// operator is told a profile went missing instead of wondering where it went.
    /// </summary>
    public IReadOnlyList<string> LoadErrors => _loadErrors;

    public IReadOnlyList<ParseProfile> List()
    {
        _loadErrors.Clear();
        var byName = new Dictionary<string, ParseProfile>(StringComparer.OrdinalIgnoreCase);

        foreach (var (directory, builtIn) in new[]
                 {
                     (BuiltInDirectory, true),
                     (UserDirectory, false)
                 })
        {
            foreach (var file in ReadJsonFiles(directory))
            {
                ParseProfile profile;
                try
                {
                    profile = Load(file, builtIn);
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    _loadErrors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                    continue;
                }

                // A profile the operator installed deliberately wins over the shipped one.
                byName[profile.Name] = profile;
            }
        }

        return byName.Values
            .OrderBy(profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public ParseProfile? Find(string name) =>
        List().FirstOrDefault(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads one profile file and checks it is usable.</summary>
    public ParseProfile Load(string filePath, bool builtIn)
    {
        var json = File.ReadAllText(filePath);
        ParseProfile profile;
        try
        {
            profile = JsonSerializer.Deserialize<ParseProfile>(json, JsonOptions)
                      ?? throw new InvalidDataException("The file is not a parse profile.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Not a valid JSON profile: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            // The file name is the fallback identity, so "my-pos.json" still imports.
            profile.Name = Path.GetFileNameWithoutExtension(filePath);
        }

        profile.FilePath = filePath;
        profile.BuiltIn = builtIn;

        var errors = Validate(profile);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(" ", errors));
        }

        return profile;
    }

    /// <summary>
    /// Checks a profile against the built-in rule set it will be merged into. Returns the
    /// same messages as configuration validation, so one list teaches both paths.
    /// </summary>
    public IReadOnlyList<string> Validate(ParseProfile profile)
    {
        if (!InterceptorConfig.IsProfileId(profile.Name))
        {
            return [$"Profile name '{profile.Name}' must be 1-40 letters, digits or dashes, starting with a letter or digit."];
        }

        var candidate = new ParseConfig();
        profile.Rules.ApplyTo(candidate);

        var errors = candidate.ValidateRules().ToList();
        if (string.IsNullOrWhiteSpace(profile.Title) && string.IsNullOrWhiteSpace(profile.Description))
        {
            errors.Add("A profile needs a title or a description so it can be recognised later.");
        }

        return errors;
    }

    /// <summary>
    /// Copies a profile file into the profiles folder. An existing profile of the same
    /// name is replaced only when <paramref name="overwrite"/> is set, so dropping a new
    /// revision of a known POS is a deliberate act.
    /// </summary>
    public string Import(string sourceFilePath, bool overwrite = false)
    {
        var profile = Load(sourceFilePath, builtIn: false);
        var existing = Directory.Exists(UserDirectory)
            ? Find(profile.Name)
            : null;
        if (existing is not null && !existing.BuiltIn && !overwrite)
        {
            throw new InvalidDataException(
                $"A profile named '{profile.Name}' is already installed at {existing.FilePath}. " +
                "Replace it explicitly to update it.");
        }

        var destination = Path.Combine(UserDirectory, profile.Name + ".json");
        Directory.CreateDirectory(UserDirectory);
        File.Copy(sourceFilePath, destination, overwrite: true);
        return destination;
    }

    /// <summary>Writes a profile into the user folder and returns its path.</summary>
    public string Save(ParseProfile profile)
    {
        var errors = Validate(profile);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(" ", errors));
        }

        Directory.CreateDirectory(UserDirectory);
        var destination = Path.Combine(UserDirectory, profile.Name + ".json");
        File.WriteAllText(destination, JsonSerializer.Serialize(profile, JsonOptions));
        profile.FilePath = destination;
        profile.BuiltIn = false;
        return destination;
    }

    /// <summary>
    /// Writes the profile's rules into the live configuration and records its name as the
    /// provenance. Applying is an explicit action, not startup behaviour: rules the
    /// operator tuned by hand must survive a restart, so they are never re-applied.
    /// </summary>
    public ParseProfile Apply(string name, InterceptorConfig config)
    {
        var profile = Find(name)
                      ?? throw new InvalidDataException(
                          $"No parse profile named '{name}' is installed. Import it into {UserDirectory} first.");

        var errors = Validate(profile);
        if (errors.Count > 0)
        {
            throw new InvalidDataException($"Profile '{name}' is not usable: {string.Join(" ", errors)}");
        }

        profile.Rules.ApplyTo(config.Parse);
        config.Parse.Profile = profile.Name;
        return profile;
    }

    /// <summary>Builds a profile from the rules a till is actually using, for sharing it.</summary>
    public static ParseProfile Capture(string name, string title, ParseConfig parse, string description = "") => new()
    {
        Name = name,
        Title = title,
        Description = string.IsNullOrWhiteSpace(description)
            ? $"Captured from a working till on {DateTime.Now:yyyy-MM-dd}."
            : description,
        Rules = parse.ExportRules()
    };

    private static IEnumerable<string> ReadJsonFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return Directory.GetFiles(directory, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
