using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Gom.Web.Previews;

/// <summary>What a fetch gave back.</summary>
public sealed record Fetched(Uri Final, string ContentType, byte[] Body, bool Cut);

/// <summary>
/// Fetches a page or a picture from the internet on behalf of the server, for people it does not know. Every address it connects
/// to is checked when the connection is made (not before, so a name that answers differently the second time cannot get past),
/// the connection goes to the very address that was checked, redirects are followed by hand and each one is checked like the
/// first address, and time, size and ports are limited. It never uses a proxy and never sends cookies or credentials.
/// </summary>
public sealed class SafeFetcher : IDisposable
{
    private readonly PreviewOptions _o;
    private readonly Func<IPAddress, bool> _allowed;
    private readonly HttpClient _client;

    public SafeFetcher(IOptions<PreviewOptions> options, Func<IPAddress, bool>? allowed = null)
    {
        _o = options.Value;
        _allowed = allowed ?? AddressGuard.IsPublic;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            MaxResponseHeadersLength = 16,                 // kilobytes
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(Math.Max(1, _o.TimeoutSeconds)),
            ConnectCallback = ConnectAsync,
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Resolves the name, keeps only the addresses that may be used, and connects to one of them.</summary>
    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;
        if (!_o.AllowedPorts.Contains(endpoint.Port)) throw new HttpRequestException("port not allowed");
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(endpoint.Host, ct);
        Exception? last = null;
        foreach (var address in addresses.Where(_allowed))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception e) when (e is not OperationCanceledException) { socket.Dispose(); last = e; }
        }
        throw new HttpRequestException(last is null ? "no allowed address" : "could not connect", last);
    }

    /// <summary>
    /// Gets the address, following a few redirects. Null when anything is wrong: a bad address, a refused one, an error, too slow,
    /// not the kind of content asked for. Nothing says why: whoever posted the link only learns that there is no preview.
    /// </summary>
    public async Task<Fetched?> GetAsync(Uri start, Func<string, bool> contentTypeOk, int maxBytes, string accept, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _o.TimeoutSeconds)));
        try
        {
            var uri = start;
            for (var hop = 0; hop <= _o.MaxRedirects; hop++)
            {
                if (!Plain(uri)) return null;
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("Accept", accept);
                request.Headers.TryAddWithoutValidation("User-Agent", "GomLinkPreview/1.0");
                request.Headers.TryAddWithoutValidation("Accept-Language", "vi, en;q=0.8");
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location;
                    if (location is null) return null;
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);   // checked again at the top of the next turn
                    continue;
                }
                if (!response.IsSuccessStatusCode) return null;
                var type = response.Content.Headers.ContentType?.MediaType ?? "";
                if (!contentTypeOk(type)) return null;

                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                var cut = false;
                int n;
                while ((n = await stream.ReadAsync(chunk, timeout.Token)) > 0)
                {
                    var room = maxBytes - (int)buffer.Length;
                    if (n >= room) { await buffer.WriteAsync(chunk.AsMemory(0, room), timeout.Token); cut = true; break; }
                    await buffer.WriteAsync(chunk.AsMemory(0, n), timeout.Token);
                }
                return new Fetched(uri, type, buffer.ToArray(), cut);
            }
            return null;   // too many redirects
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or UriFormatException or InvalidOperationException or SocketException)
        {
            if (ct.IsCancellationRequested) throw;
            return null;
        }
    }

    /// <summary>Only http and https, with a host and no name or password in front of it, on an allowed port.</summary>
    private bool Plain(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme is "http" or "https" && uri.Host.Length > 0 && uri.UserInfo.Length == 0 && _o.AllowedPorts.Contains(uri.Port);

    public void Dispose() => _client.Dispose();
}
