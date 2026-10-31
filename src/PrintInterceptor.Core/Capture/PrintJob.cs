namespace PrintInterceptor.Core.Capture;

public sealed record PrintJob(byte[] Data, string RemoteEndPoint, DateTimeOffset ReceivedAt);

public interface IJobProcessor
{
    Task ProcessAsync(PrintJob job, CancellationToken cancellationToken);
}
