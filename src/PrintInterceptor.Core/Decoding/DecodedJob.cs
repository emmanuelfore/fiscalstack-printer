namespace PrintInterceptor.Core.Decoding;

public enum JobFormat
{
    Text,
    PostScript,
    Pdf
}

public sealed record DecodedJob(
    bool Success,
    JobFormat Format,
    string Text,
    string? JobTitle,
    string? Error)
{
    public static DecodedJob Failure(JobFormat format, string error) =>
        new(false, format, string.Empty, null, error);
}
