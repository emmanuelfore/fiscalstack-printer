using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PrintInterceptor.Core.Tests;

public sealed record StubRequest(string Method, string Path, string? Query, string? ApiKey, string Body);

public sealed record StubResponse(int Status, string ContentType, byte[] Body);

/// <summary>
/// Minimal loopback HTTP server for the API-client tests. A raw socket avoids the HttpListener
/// URL ACL that fails for non-admin test runs on Windows. The responder gets the raw request
/// target, query string included, so a test can tell one page of a paged read from the next.
/// </summary>
public sealed class StubHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly Func<string, StubResponse> _respond;
    private readonly List<StubRequest> _requests = new();

    public StubHttpServer(Func<string, StubResponse> respond)
    {
        _respond = respond;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public IReadOnlyList<StubRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    public static StubResponse Json(int status, string body) =>
        new(status, "application/json", Encoding.UTF8.GetBytes(body));

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);

                var requestLine = await reader.ReadLineAsync() ?? "";
                var parts = requestLine.Split(' ');
                var method = parts.ElementAtOrDefault(0) ?? "";
                var target = parts.ElementAtOrDefault(1) ?? "/";
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                {
                    var separator = line.IndexOf(':');
                    if (separator > 0)
                    {
                        headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                    }
                }

                var body = "";
                if (headers.TryGetValue("Content-Length", out var lengthText) &&
                    int.TryParse(lengthText, out var length) && length > 0)
                {
                    var buffer = new char[length];
                    var read = 0;
                    while (read < length)
                    {
                        var chunk = await reader.ReadAsync(buffer.AsMemory(read, length - read));
                        if (chunk == 0)
                        {
                            break;
                        }

                        read += chunk;
                    }

                    body = new string(buffer, 0, read);
                }

                var separator2 = target.IndexOf('?');
                lock (_requests)
                {
                    _requests.Add(new StubRequest(
                        method,
                        separator2 < 0 ? target : target[..separator2],
                        separator2 < 0 ? null : target[(separator2 + 1)..],
                        headers.TryGetValue("X-API-Key", out var key) ? key : null,
                        body));
                }

                var response = _respond(target);
                var head =
                    $"HTTP/1.1 {response.Status} {(response.Status == 200 ? "OK" : "Error")}\r\n" +
                    $"Content-Type: {response.ContentType}\r\n" +
                    $"Content-Length: {response.Body.Length}\r\n" +
                    "Connection: close\r\n\r\n";

                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(response.Body);
                await stream.FlushAsync();
            }
            catch
            {
                // The client aborted; nothing to do in a stub.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch
        {
            // Ignored while shutting the stub down.
        }

        _cts.Dispose();
    }
}
