using System.Text.Json;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;

namespace PrintInterceptor.Core.Fiscalisation;

/// <summary>
/// Keeps the tenant identity that receipts print. FiscalStack is authoritative;
/// config.json remains the offline fallback when provisioning has never
/// succeeded. The downloaded logo is stored beside the cache so printing works
/// without a network.
/// </summary>
public sealed class CompanyProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] LogoExtensions = { ".png", ".jpg", ".gif", ".bmp" };

    private readonly InterceptorConfig _config;
    private readonly ILog _log;
    private readonly string _directory;
    private readonly string _cachePath;
    private readonly object _gate = new();

    private CompanyProfile? _profile;
    private string? _logoPath;

    public CompanyProfileService(InterceptorConfig config, ILog log)
    {
        _config = config;
        _log = log;
        _directory = Path.Combine(config.ResolveDataDirectory(), "state");
        _cachePath = Path.Combine(_directory, "company-profile.json");
        LoadCached();
    }

    public bool IsProvisioned
    {
        get
        {
            lock (_gate)
            {
                return _profile is not null;
            }
        }
    }

    public CompanyProfile? Profile
    {
        get
        {
            lock (_gate)
            {
                return _profile;
            }
        }
    }

    public string Name => Pick(_profile?.Name, _config.Company.Name);
    public string Tin => Pick(_profile?.Tin, _config.Company.Tin);
    public string VatNumber => Pick(_profile?.VatNumber, _config.Company.VatNumber);
    public string Phone => Pick(_profile?.Phone, _config.Company.Phone);
    public string Email => Pick(_profile?.Email, _config.Company.Email);

    public string Address
    {
        get
        {
            var profile = _profile;
            if (profile is null)
            {
                return _config.Company.Address;
            }

            if (string.IsNullOrWhiteSpace(profile.Address))
            {
                return Pick(profile.City, _config.Company.Address);
            }

            if (string.IsNullOrWhiteSpace(profile.City) ||
                profile.Address.EndsWith(profile.City, StringComparison.OrdinalIgnoreCase))
            {
                return profile.Address;
            }

            return $"{profile.Address}, {profile.City}";
        }
    }

    /// <summary>
    /// Prefers the logo provisioned from FiscalStack, then the local file.
    /// </summary>
    public string? ResolveLogoPath(string? configuredLogoPath)
    {
        lock (_gate)
        {
            if (_logoPath is not null && File.Exists(_logoPath))
            {
                return _logoPath;
            }
        }

        return string.IsNullOrWhiteSpace(configuredLogoPath) ? null : configuredLogoPath;
    }

    public async Task<bool> RefreshAsync(FiscalStackClient client, CancellationToken cancellationToken)
    {
        var profile = await client.GetCompanyProfileAsync(cancellationToken);
        if (profile is null)
        {
            return false;
        }

        var previousLogo = _logoPath;
        var logoPath = await ResolveLogoAsync(client, profile, previousLogo, cancellationToken);

        lock (_gate)
        {
            _profile = profile;
            _logoPath = logoPath;
        }

        Persist(profile, logoPath);
        _log.Info(
            $"Company identity provisioned from FiscalStack: {Name} TIN={Tin} VAT={VatNumber}" +
            (logoPath is null ? "" : $" logo={Path.GetFileName(logoPath)}") + ".");
        return true;
    }

    private async Task<string?> ResolveLogoAsync(
        FiscalStackClient client,
        CompanyProfile profile,
        string? fallbackLogoPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.LogoUrl))
        {
            return null;
        }

        var bytes = await client.DownloadImageAsync(profile.LogoUrl, cancellationToken);
        if (bytes is null)
        {
            return fallbackLogoPath;
        }

        var extension = DetectExtension(bytes);
        if (extension is null)
        {
            _log.Warn("The provisioned logo is not a PNG/JPG/GIF/BMP image; keeping the local logo.");
            return fallbackLogoPath;
        }

        try
        {
            Directory.CreateDirectory(_directory);
            var target = Path.Combine(_directory, "company-logo" + extension);
            var temp = target + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, target, overwrite: true);

            foreach (var other in LogoExtensions.Where(value => value != extension))
            {
                var stale = Path.Combine(_directory, "company-logo" + other);
                if (File.Exists(stale))
                {
                    File.Delete(stale);
                }
            }

            return target;
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not store the provisioned logo: {ex.Message}");
            return fallbackLogoPath;
        }
    }

    private void LoadCached()
    {
        if (!File.Exists(_cachePath))
        {
            return;
        }

        try
        {
            var cache = JsonSerializer.Deserialize<CompanyProfileCache>(
                File.ReadAllText(_cachePath), JsonOptions);
            if (cache?.Profile is null)
            {
                return;
            }

            var logo = string.IsNullOrWhiteSpace(cache.LogoFile)
                ? null
                : Path.Combine(_directory, cache.LogoFile);
            _profile = cache.Profile;
            _logoPath = logo is not null && File.Exists(logo) ? logo : null;
            _log.Info($"Company identity restored from cache: {_profile.Name}.");
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not read the cached company profile: {ex.Message}");
        }
    }

    private void Persist(CompanyProfile profile, string? logoPath)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var json = JsonSerializer.Serialize(
                new CompanyProfileCache
                {
                    Profile = profile,
                    LogoFile = logoPath is null ? null : Path.GetFileName(logoPath),
                    FetchedAt = DateTimeOffset.Now
                },
                JsonOptions);

            var temp = _cachePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _cachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not cache the company identity: {ex.Message}");
        }
    }

    private static string? DetectExtension(byte[] bytes)
    {
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return ".png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return ".jpg";
        }

        if (bytes.Length >= 6 &&
            bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
        {
            return ".gif";
        }

        if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D)
        {
            return ".bmp";
        }

        return null;
    }

    private static string Pick(string? provisioned, string fallback) =>
        string.IsNullOrWhiteSpace(provisioned) ? fallback : provisioned!;

    private sealed class CompanyProfileCache
    {
        public CompanyProfile Profile { get; set; } = new();
        public string? LogoFile { get; set; }
        public DateTimeOffset FetchedAt { get; set; }
    }
}
