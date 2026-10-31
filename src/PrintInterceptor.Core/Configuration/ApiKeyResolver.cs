namespace PrintInterceptor.Core.Configuration;

public enum ApiKeyOrigin
{
    None,
    Store,
    Environment,
    ConfigurationFile
}

/// <param name="Key">The resolved key, or null when nothing is configured.</param>
/// <param name="Origin">Which source provided it.</param>
/// <param name="Detail">Where that source is, for the startup log.</param>
public sealed record ApiKeyResolution(string? Key, ApiKeyOrigin Origin, string? Detail)
{
    public bool Found => !string.IsNullOrWhiteSpace(Key);
}

/// <summary>
/// Single place that decides which API key the process uses. Precedence is
/// deliberate: a key registered on this till (the secret store) beats a machine
/// environment variable, which in turn beats a value left over in
/// <c>config.json</c>. Registering a key on the till therefore always wins over
/// stale configuration an imaging tool may have left behind.
/// </summary>
public static class ApiKeyResolver
{
    public const string EnvironmentVariable = "PRINTINTERCEPTOR_API_KEY";

    public static ApiKeyResolution Resolve(
        InterceptorConfig config,
        SecretStore store,
        string? environmentValue)
    {
        if (store.TryGet(SecretStore.ApiKeyEntry, out var stored) &&
            !string.IsNullOrWhiteSpace(stored))
        {
            return new ApiKeyResolution(stored.Trim(), ApiKeyOrigin.Store, store.DirectoryPath);
        }

        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return new ApiKeyResolution(
                environmentValue.Trim(), ApiKeyOrigin.Environment, EnvironmentVariable);
        }

        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return new ApiKeyResolution(
                config.ApiKey.Trim(), ApiKeyOrigin.ConfigurationFile, config.ConfigPath);
        }

        return new ApiKeyResolution(null, ApiKeyOrigin.None, null);
    }
}
