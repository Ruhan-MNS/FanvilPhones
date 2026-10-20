using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using FancilPhones.Application;

namespace FancilPhones.Infrastructure.Services;

/// <summary>
/// Scans an IP range for Fanvil phones by reusing <see cref="FanvilHttpClient"/>'s
/// raw-socket probe (a Fanvil's embedded "Rapid Logic" server returns a login
/// page containing "logonButton" and exposes a hex nonce at /key==nonce).
/// </summary>
public sealed class PhoneDiscoveryService(IUserAccess access) : IPhoneDiscoveryService
{
    /// <summary>
    /// Probe one host: TCP-connect port 80, then check for the Fanvil login page
    /// signature. Returns a result for every IP (including non-Fanvil / unreachable
    /// hosts) so callers can show progress.
    /// </summary>
    public async Task<DiscoveredPhone> ProbeOneAsync(
        string ip,
        HashSet<string> knownIps,
        TimeSpan tcpTimeout,
        TimeSpan httpTimeout,
        CancellationToken ct)
    {
        await access.RequireAdministratorAsync();
        var alreadyKnown = knownIps.Contains(ip);

        // Step 1: cheap TCP connect; skip dead hosts fast.
        try
        {
            using var tcp = new TcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(tcpTimeout);
            await tcp.ConnectAsync(ip, 80, connectCts.Token);
        }
        catch
        {
            return new DiscoveredPhone(ip, false, alreadyKnown, "No HTTP on port 80");
        }

        // Step 2: multi-signal HTTP check. Fanvil web UI is frame-based:
        //   GET /          -> <frameset> shell (no login form text)
        //   GET /login.htm -> the actual login frame containing logonButton, USER_PASSWORD_ERROR
        // We reuse the lenient FanvilHttpClient (raw socket, tolerant parser, keeps
        // one persistent TCP connection across both requests). We deliberately do NOT
        // touch /key==nonce — that consumes a phone session slot and the endpoint is
        // flaky; scanning a /24 with it would burn slots on real handsets in the fleet.
        try
        {
            using var http = new FanvilHttpClient(ip, useTls: false);
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reqCts.CancelAfter(httpTimeout);

            var rootResp = await http.GetAsync("/", reqCts.Token);
            var rootBody = rootResp.Body ?? "";

            if (IsFanvilBody(rootBody))
                return new DiscoveredPhone(ip, true, alreadyKnown,
                    alreadyKnown ? "Already added" : "Fanvil phone detected (root)");

            // Frame markers tell us this is *probably* a frame-based device — likely Fanvil.
            // Confirm by fetching the login frame; non-Fanvil hosts return 404 / unrelated bodies.
            var looksFrameBased = rootBody.Contains("<frameset", StringComparison.OrdinalIgnoreCase)
                                  || rootBody.Contains("<frame", StringComparison.OrdinalIgnoreCase);

            using var reqCts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reqCts2.CancelAfter(httpTimeout);
            FanvilResponse? loginResp = null;
            try
            {
                loginResp = await http.GetAsync("/login.htm", reqCts2.Token);
            }
            catch
            {
                // login.htm probe failed — fall through with whatever we have from root.
            }

            if (loginResp is not null)
            {
                var loginBody = loginResp.Body ?? "";
                if (IsFanvilBody(loginBody))
                    return new DiscoveredPhone(ip, true, alreadyKnown,
                        alreadyKnown ? "Already added" : "Fanvil phone detected (login.htm)");

                // /login.htm returned something Fanvil-ish but not the form text directly.
                if (looksFrameBased && loginResp.StatusCode is 200 or 0 && loginBody.Length > 0)
                    return new DiscoveredPhone(ip, true, alreadyKnown,
                        alreadyKnown ? "Already added" : "Fanvil phone detected (frames + /login.htm)");
            }

            var snippet = Snippet(rootBody);
            return new DiscoveredPhone(ip, false, alreadyKnown,
                $"HTTP {rootResp.StatusCode}, no Fanvil signature. {snippet}");
        }
        catch (Exception ex)
        {
            return new DiscoveredPhone(ip, false, alreadyKnown, $"Probe error: {ex.Message}");
        }
    }

    private static bool IsFanvilBody(string body)
    {
        if (string.IsNullOrEmpty(body)) return false;
        // Strings that appear in the Fanvil X-series login frame and assorted JS:
        string[] markers =
        {
            "logonButton", "USER_PASSWORD_ERROR", "Fanvil", "RapidLogic",
            "title.htm", "mainfrm", "Loginchk", "CurLanguage",
        };
        foreach (var m in markers)
            if (body.Contains(m, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string Snippet(string body)
    {
        if (string.IsNullOrEmpty(body)) return "empty body";
        var trimmed = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (trimmed.Length == 0) return "empty body";
        return trimmed.Length <= 80
            ? $"Body: {trimmed}"
            : $"Body: {trimmed[..80]}...";
    }

    /// <summary>
    /// Stream discovery results as each probe completes.
    /// </summary>
    public async IAsyncEnumerable<DiscoveredPhone> ScanAsync(
        string cidrOrRange,
        HashSet<string> knownIps,
        int maxConcurrency = 32,
        TimeSpan? tcpTimeout = null,
        TimeSpan? httpTimeout = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await access.RequireAdministratorAsync();
        var tcpT = tcpTimeout ?? TimeSpan.FromMilliseconds(600);
        var httpT = httpTimeout ?? TimeSpan.FromSeconds(4);

        var channel = Channel.CreateUnbounded<DiscoveredPhone>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        var sem = new SemaphoreSlim(maxConcurrency);
        var ips = PhoneNetworkRange.EnumerateIps(cidrOrRange).ToList();

        _ = Task.Run(async () =>
        {
            try
            {
                var tasks = new List<Task>(ips.Count);
                foreach (var ip in ips)
                {
                    if (ct.IsCancellationRequested) break;
                    await sem.WaitAsync(ct);
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var result = await ProbeOneAsync(ip, knownIps, tcpT, httpT, ct);
                            await channel.Writer.WriteAsync(result, ct);
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            try { await channel.Writer.WriteAsync(
                                new DiscoveredPhone(ip, false, knownIps.Contains(ip),
                                    $"Probe error: {ex.Message}"), ct); }
                            catch { }
                        }
                        finally { sem.Release(); }
                    }, ct));
                }
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) { }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, ct);

        await foreach (var item in channel.Reader.ReadAllAsync(ct))
            yield return item;
    }
}
