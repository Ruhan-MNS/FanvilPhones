using System.Runtime.CompilerServices;
using System.Threading.Channels;

using FancilPhones.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace FancilPhones.Infrastructure.Services;

/// <summary>
/// Scans an IP range for Fanvil phones by reusing <see cref="FanvilHttpClient"/>'s
/// raw-socket probe (a Fanvil's embedded "Rapid Logic" server returns a login
/// page containing "logonButton" and exposes a hex nonce at /key==nonce).
/// </summary>
public sealed class PhoneDiscoveryService(IUserAccess access, ILogger<PhoneDiscoveryService> logger, IFanvilClientFactory clients,
    IHostApplicationLifetime application) : IPhoneDiscoveryService
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
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, application.ApplicationStopping);
        ct = stopping.Token;
        await access.RequireAdministratorAsync(ct);
        return await ProbeCoreAsync(ip, knownIps, tcpTimeout, httpTimeout, ct);
    }

    private async Task<DiscoveredPhone> ProbeCoreAsync(string ip, HashSet<string> knownIps,
        TimeSpan tcpTimeout, TimeSpan httpTimeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var alreadyKnown = knownIps.Contains(ip);

        // Step 2: multi-signal HTTP check. Fanvil web UI is frame-based:
        //   GET /          -> <frameset> shell (no login form text)
        //   GET /login.htm -> the actual login frame containing logonButton, USER_PASSWORD_ERROR
        // We reuse the lenient FanvilHttpClient (raw socket, tolerant parser, keeps
        // one persistent TCP connection across both requests). We deliberately do NOT
        // touch /key==nonce — that consumes a phone session slot and the endpoint is
        // flaky; scanning a /24 with it would burn slots on real handsets in the fleet.
        try
        {
            using var http = clients.Create(ip, useTls: false, connectTimeout: tcpTimeout);
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Handset discovery probe failed for {IpAddress}.", ip);
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
        await access.RequireAdministratorAsync(ct);
        var tcpT = tcpTimeout ?? TimeSpan.FromMilliseconds(600);
        var httpT = httpTimeout ?? TimeSpan.FromSeconds(4);

        if (maxConcurrency is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        if (tcpT <= TimeSpan.Zero || httpT <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(tcpTimeout));
        var ips = PhoneNetworkRange.EnumerateIps(cidrOrRange).Take(4097).ToArray();
        if (ips.Length > 4096) throw new ArgumentException("Choose a range with 4,096 addresses or fewer.", nameof(cidrOrRange));
        var known = new HashSet<string>(knownIps, StringComparer.OrdinalIgnoreCase);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, application.ApplicationStopping);
        var token = stop.Token;
        var channel = Channel.CreateBounded<DiscoveredPhone>(new BoundedChannelOptions(maxConcurrency * 2)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
        });
        using var scope = logger.BeginScope("DiscoveryId:{DiscoveryId}", Guid.NewGuid());
        logger.LogInformation("Network discovery started for {AddressCount} addresses with concurrency {Concurrency}.", ips.Length, maxConcurrency);
        var checkedCount = 0;
        var detectedCount = 0;
        var producer = ProduceAsync();
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(token))
            {
                checkedCount++;
                if (item.IsFanvil) detectedCount++;
                yield return item;
            }
            logger.LogInformation("Network discovery completed. Checked: {CheckedCount}; detected: {DetectedCount}.", checkedCount, detectedCount);
        }
        finally
        {
            // Early consumer disposal is cancellation too: join all workers before returning.
            await stop.CancelAsync();
            await producer;
            if (ct.IsCancellationRequested)
                logger.LogInformation("Network discovery canceled after {CheckedCount} addresses; detected: {DetectedCount}.", checkedCount, detectedCount);
        }

        async Task ProduceAsync()
        {
            Exception? failure = null;
            try
            {
                await Parallel.ForEachAsync(ips, new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxConcurrency, CancellationToken = token
                }, async (ip, workerToken) =>
                {
                    var result = await ProbeCoreAsync(ip, known, tcpT, httpT, workerToken);
                    await channel.Writer.WriteAsync(result, workerToken);
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                failure = ex;
                logger.LogError(ex, "Network discovery failed.");
            }
            finally { channel.Writer.TryComplete(failure); }
        }
    }
}
