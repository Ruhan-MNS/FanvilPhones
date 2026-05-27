using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace FancilPhones.Services;

/// <summary>
/// A deliberately lenient HTTP/1.1 client for Fanvil IP phones.
/// Their embedded "Rapid Logic" web server emits non-RFC-compliant responses
/// that the framework's HttpClient rejects, authenticates via a nonce + MD5
/// challenge (no HTTP Basic auth), and ties the login nonce to a single TCP
/// connection - so this client keeps ONE persistent connection alive for the
/// whole login/upload sequence and parses responses tolerantly over a raw socket.
/// </summary>
public sealed class FanvilResponse
{
    public int StatusCode { get; init; }
    public string Body { get; init; } = "";
    public List<string> SetCookies { get; } = new();

    /// <summary>First ~500 bytes of the raw response, control chars escaped (for diagnostics).</summary>
    public string RawDump { get; init; } = "";
}

public sealed class FanvilHttpClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly bool _useTls;

    /// <summary>Cookies accumulated across requests, resent on every call.</summary>
    private readonly Dictionary<string, string> _cookies = new(StringComparer.OrdinalIgnoreCase);

    private TcpClient? _tcp;
    private Stream? _stream;

    public FanvilHttpClient(string host, bool useTls, int? port = null)
    {
        _host = host;
        _useTls = useTls;
        _port = port ?? (useTls ? 443 : 80);
    }

    public Task<FanvilResponse> GetAsync(string path, CancellationToken ct = default)
    {
        return SendAsync("GET", path, null, null, ct);
    }

    /// <summary>
    /// Performs the Fanvil nonce/MD5 login. On success the session 'auth' cookie
    /// is held for subsequent calls. Returns true on success.
    /// </summary>
    public async Task<(bool ok, string message)> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        // The phone's nonce endpoint is flaky (it intermittently returns an empty
        // body), so retry the whole login a few times on a fresh connection.
        const int maxAttempts = 4;
        var lastMessage = "Login failed.";

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (attempt > 1)
            {
                CloseConnection();         // force a brand-new TCP connection
                _cookies.Remove("auth");
                await Task.Delay(750 * attempt, ct);
            }

            // All three steps run over the SAME persistent TCP connection.
            await GetAsync("/", ct);

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var nonceResp = await GetAsync($"/key==nonce?now={now}", ct);
            var nonce = nonceResp.Body.Trim();
            if (nonce.Length is < 8 or > 64)
            {
                lastMessage = $"Unexpected nonce response (HTTP {nonceResp.StatusCode}, " + $"body {nonce.Length} chars). RAW: {nonceResp.RawDump}";
                continue; // retry
            }

            // The phone's JS stores the nonce as the 'auth' cookie before posting.
            _cookies["auth"] = nonce;

            var digest = Md5Hex($"{username}:{password}:{nonce}");
            
            var encoded = $"{username}:{digest}";

            var resp = await PostFormAsync("/", new Dictionary<string, string>
            {
                ["encoded"] = encoded,
                ["CurLanguage"] = "en",
                ["ReturnPage"] = "/",
            }, ct);

            var stillOnLogin = resp.Body.Contains("logonButton", StringComparison.OrdinalIgnoreCase)
                               || resp.Body.Contains("USER_PASSWORD_ERROR", StringComparison.OrdinalIgnoreCase);
            if (stillOnLogin)
                return (false, "Login rejected - check username/password.");

            return (true, attempt == 1 ? "Login OK" : $"Login OK (after {attempt} attempts)");
        }

        return (false, $"{lastMessage} (gave up after {maxAttempts} attempts)");
    }

    /// <summary>
    /// Logs the session out so the phone frees its session slot. Best-effort:
    /// failures are swallowed. Mirrors the web UI's Logout button
    /// (POST /title.htm with DefaultLogout=Logout).
    /// </summary>
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        try
        {
            await PostFormAsync("/title.htm", new Dictionary<string, string> { ["DefaultLogout"] = "Logout" }, ct);
        }
        catch { /* best-effort */ }
        finally
        {
            _cookies.Remove("auth");
        }
    }

    /// <summary>
    /// Deletes every contact in the phone's local phonebook, mirroring the
    /// Contacts tab's "Delete All" button (POST /contacts.htm, DefaultDeleteAll).
    /// </summary>
    public Task<FanvilResponse> DeleteAllContactsAsync(CancellationToken ct = default)
        => PostFormAsync("/contacts.htm", new Dictionary<string, string>
        {
            ["ReturnPage"] = "/contacts.htm",
            ["PHB_SelectCheckBox_Sets"] = "",
            ["PHB_GroupIndex"] = "",
            ["DefaultDeleteAll"] = "Delete All",
        }, ct);

    public Task<FanvilResponse> PostFormAsync(string path, IReadOnlyDictionary<string, string> fields, CancellationToken ct = default)
    {
        // application/x-www-form-urlencoded encodes spaces as '+' (not %20).
        var body = string.Join("&", fields.Select(kv =>
            $"{FormEncode(kv.Key)}={FormEncode(kv.Value)}"));
        return SendAsync("POST", path, "application/x-www-form-urlencoded",
            Encoding.ASCII.GetBytes(body), ct);
    }

    /// <summary>POST a single file as multipart/form-data under the given field name.</summary>
    public Task<FanvilResponse> PostFileAsync(string path, string fieldName, string fileName, byte[] fileBytes, string fileContentType, CancellationToken ct = default)
    {
        var boundary = "----FancilPhones" + Guid.NewGuid().ToString("N");
        var pre = new StringBuilder();
        pre.Append("--").Append(boundary).Append("\r\n");
        pre.Append("Content-Disposition: form-data; name=\"").Append(fieldName)
           .Append("\"; filename=\"").Append(fileName).Append("\"\r\n");
        pre.Append("Content-Type: ").Append(fileContentType).Append("\r\n\r\n");
        var post = "\r\n--" + boundary + "--\r\n";

        var body = new byte[Encoding.ASCII.GetByteCount(pre.ToString())
                            + fileBytes.Length
                            + Encoding.ASCII.GetByteCount(post)];
        var i = Encoding.ASCII.GetBytes(pre.ToString(), 0, pre.Length, body, 0);
        Buffer.BlockCopy(fileBytes, 0, body, i, fileBytes.Length);
        Encoding.ASCII.GetBytes(post, 0, post.Length, body, i + fileBytes.Length);

        return SendAsync("POST", path, "multipart/form-data; boundary=" + boundary, body, ct);
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_tcp is { Connected: true } && _stream is not null)
            return;

        CloseConnection();
        _tcp = new TcpClient { ReceiveTimeout = 30000, SendTimeout = 30000, NoDelay = true };
        await _tcp.ConnectAsync(_host, _port, ct);

        Stream s = _tcp.GetStream();
        if (_useTls)
        {
            var ssl = new SslStream(s, false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(_host);
            s = ssl;
        }
        _stream = s;
    }

    private async Task<FanvilResponse> SendAsync(string method, string path, string? contentType, byte[]? body, CancellationToken ct)
    {
        // One reconnect retry: a kept-alive connection may have been closed by the
        // server between requests.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await EnsureConnectedAsync(ct);
                return await SendOnceAsync(method, path, contentType, body, ct);
            }
            catch (Exception) when (attempt == 0)
            {
                CloseConnection();
            }
        }
    }

    private async Task<FanvilResponse> SendOnceAsync(string method, string path, string? contentType, byte[]? body, CancellationToken ct)
    {
        var stream = _stream!;

        var head = new StringBuilder();
        head.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
        head.Append("Host: ").Append(_host).Append("\r\n");
        head.Append("User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) FancilPhones/1.0\r\n");
        head.Append("Accept: */*\r\n");
        head.Append("Accept-Encoding: identity\r\n");
        // Send Referer matching the path we're hitting; some Fanvil firmwares
        // silently reject form POSTs whose Referer doesn't match the form page
        // (e.g. /lines.htm needs Referer: .../lines.htm, not .../).
        var refererPath = path == "/" ? "/" : path.Split('?')[0];
        head.Append("Referer: http://").Append(_host).Append(refererPath).Append("\r\n");
        if (_cookies.Count > 0)
        {
            head.Append("Cookie: ");
            head.Append(string.Join("; ", _cookies.Select(kv => $"{kv.Key}={kv.Value}")));
            head.Append("\r\n");
        }
        if (body is not null)
        {
            head.Append("Content-Type: ").Append(contentType).Append("\r\n");
            head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        }
        head.Append("Connection: keep-alive\r\n\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
        if (body is not null)
            await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);

        // Read exactly one response. Bounded by Content-Length when present;
        // otherwise by a 3s idle gap (the connection stays open under keep-alive).
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        var headerEnd = -1;
        var contentLength = -1;
        while (true)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(ms.Length > 0 ? 3000 : 15000);

            int read;
            try
            {
                read = await stream.ReadAsync(buf, readCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                break; // idle timeout - response finished
            }

            if (read <= 0) break; // connection closed by server
            ms.Write(buf, 0, read);

            if (headerEnd < 0)
                TryFindHeaderEnd(ms.GetBuffer(), (int)ms.Length, out headerEnd, out contentLength);

            if (headerEnd >= 0 && contentLength >= 0 && ms.Length - headerEnd >= contentLength)
                break; // full body received
        }

        if (ms.Length == 0)
            throw new Exception("Phone closed the connection without sending a response.");

        return Parse(ms.ToArray());
    }

    private static bool TryFindHeaderEnd(byte[] data, int len, out int headerEnd, out int contentLength)
    {
        headerEnd = -1;
        contentLength = -1;
        var pos = 0;
        var headerLines = new List<string>();
        while (pos < len)
        {
            var lineEnd = pos;
            while (lineEnd < len && data[lineEnd] != (byte)'\n' && data[lineEnd] != (byte)'\r')
                lineEnd++;
            if (lineEnd >= len) return false; // line not terminated yet

            var line = Encoding.ASCII.GetString(data, pos, lineEnd - pos);
            var next = lineEnd;
            if (next < len && data[next] == (byte)'\r') next++;
            if (next < len && data[next] == (byte)'\n') next++;

            if (line.Length == 0)
            {
                headerEnd = next;
                foreach (var h in headerLines)
                {
                    var c = h.IndexOf(':');
                    if (c > 0 && h[..c].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(h[(c + 1)..].Trim(), out contentLength);
                }
                return true;
            }
            headerLines.Add(line);
            pos = next;
        }
        return false;
    }

    private FanvilResponse Parse(byte[] raw)
    {
        var lines = new List<string>();
        var pos = 0;
        var bodyStart = raw.Length;
        while (pos < raw.Length)
        {
            var lineEnd = pos;
            while (lineEnd < raw.Length && raw[lineEnd] != (byte)'\n' && raw[lineEnd] != (byte)'\r')
                lineEnd++;
            var line = Encoding.ASCII.GetString(raw, pos, lineEnd - pos);

            var next = lineEnd;
            if (next < raw.Length && raw[next] == (byte)'\r') next++;
            if (next < raw.Length && raw[next] == (byte)'\n') next++;

            if (line.Length == 0) { bodyStart = next; break; }
            lines.Add(line);
            pos = next;
            if (next == lineEnd) break;
        }

        var bodyBytes = bodyStart < raw.Length ? raw[bodyStart..] : Array.Empty<byte>();

        var status = 0;
        if (lines.Count > 0)
        {
            var parts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].StartsWith("HTTP", StringComparison.OrdinalIgnoreCase))
                int.TryParse(parts[1], out status);
        }

        var dumpLen = Math.Min(raw.Length, 500);
        var dump = new StringBuilder();
        for (var k = 0; k < dumpLen; k++)
        {
            var b = raw[k];
            dump.Append(b switch
            {
                (byte)'\r' => "\\r",
                (byte)'\n' => "\\n\n",
                >= 32 and < 127 => ((char)b).ToString(),
                _ => $"\\x{b:x2}"
            });
        }

        var resp = new FanvilResponse
        {
            StatusCode = status == 0 ? 200 : status,
            Body = Encoding.UTF8.GetString(bodyBytes),
            RawDump = dump.ToString()
        };

        foreach (var line in lines.Skip(1))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var name = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (!name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)) continue;

            resp.SetCookies.Add(value);
            var pair = value.Split(';', 2)[0];
            var eq = pair.IndexOf('=');
            if (eq > 0)
                _cookies[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
        }

        return resp;
    }

    private static string FormEncode(string s) => Uri.EscapeDataString(s).Replace("%20", "+");

    private static string Md5Hex(string s)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void CloseConnection()
    {
        try { _stream?.Dispose(); } catch { /* ignore */ }
        try { _tcp?.Dispose(); } catch { /* ignore */ }
        _stream = null;
        _tcp = null;
    }

    public void Dispose() => CloseConnection();
}
