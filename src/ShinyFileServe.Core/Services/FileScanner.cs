using Microsoft.Extensions.Logging;

namespace ShinyFileServe.Core.Services;

public enum ScanVerdict
{
    /// <summary>An engine looked at the bytes and found nothing.</summary>
    Clean,

    /// <summary>An engine matched a signature.</summary>
    Infected,

    /// <summary>Deliberately not scanned - no engine is configured, or the file is over the size cap.</summary>
    Skipped,

    /// <summary>An engine was configured but could not answer: down, unreachable, or too slow.</summary>
    Unavailable
}

/// <summary>What one engine made of one file.</summary>
public record ScanResult(ScanVerdict Verdict, string? Detail = null)
{
    public static readonly ScanResult Clean = new(ScanVerdict.Clean);
    public static ScanResult Infected(string signature) => new(ScanVerdict.Infected, signature);
    public static ScanResult Skipped(string why) => new(ScanVerdict.Skipped, why);
    public static ScanResult Unavailable(string why) => new(ScanVerdict.Unavailable, why);
}

/// <summary>
/// One antimalware engine. Implementations are thin clients over something that already exists on
/// the machine - there is no scanner written in .NET, only ways of asking one that is already there.
/// </summary>
public interface IFileScanner
{
    /// <summary>Shown in the log and the audit trail, so it says which engine had the opinion.</summary>
    string Name { get; }

    /// <summary>
    /// Scans the file at <paramref name="path"/>. <paramref name="displayName"/> is the name the
    /// file will have once it lands - the path is a temporary one and tells an engine nothing.
    /// </summary>
    Task<ScanResult> Scan(string path, string displayName, CancellationToken ct = default);

    /// <summary>Is the engine there at all? Used once at startup so a misconfiguration is loud.</summary>
    Task<bool> IsAvailable(CancellationToken ct = default);
}

public class FileScanOptions
{
    /// <summary>
    /// Files larger than this are not scanned. clamd refuses a stream over its own
    /// <c>StreamMaxLength</c> - 25MB out of the box - and AMSI wants the whole file in memory, so a
    /// cap here is the honest version of a limit that exists whether or not it is written down.
    /// 0 means no cap, which only makes sense with a local clamd scanning by path.
    /// </summary>
    public long MaxScanBytes { get; set; } = 25L * 1024 * 1024;

    /// <summary>
    /// Refuse an upload that could not be scanned - too large, or every engine down. Off by default:
    /// a file server whose uploads stop working because a daemon died is its own kind of outage, and
    /// which of the two is worse is the operator's call, not ours.
    /// </summary>
    public bool RejectUnscannable { get; set; }

    /// <summary>How long one file gets before the engine is treated as unavailable.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>The answer the upload path acts on: whether the file may land, and why not if not.</summary>
public record ScanDecision(bool Allowed, ScanVerdict Verdict, string? Engine, string? Detail)
{
    /// <summary>What goes in the audit log and back to the caller.</summary>
    public string Describe() => this.Verdict switch
    {
        ScanVerdict.Infected => $"Malware detected by {this.Engine}: {this.Detail}",
        ScanVerdict.Unavailable => $"Could not be scanned: {this.Detail}",
        ScanVerdict.Skipped => $"Could not be scanned: {this.Detail}",
        _ => "Clean"
    };
}

/// <summary>
/// Runs the registered engines over a finished upload and applies the policy. Always resolvable:
/// with no engines registered every file comes back skipped, which is the behaviour of a server
/// that was never told to scan anything.
/// </summary>
public interface IUploadScanner
{
    bool IsEnabled { get; }

    Task<ScanDecision> Scan(string path, string displayName, CancellationToken ct = default);

    /// <summary>One line per engine, for the startup log.</summary>
    Task<IReadOnlyList<string>> Describe(CancellationToken ct = default);
}

public class UploadScanner(
    IEnumerable<IFileScanner> scanners,
    FileScanOptions options,
    ILogger<UploadScanner> logger
) : IUploadScanner
{
    readonly IReadOnlyList<IFileScanner> engines = scanners.ToList();

    public bool IsEnabled => this.engines.Count > 0;

    public async Task<ScanDecision> Scan(string path, string displayName, CancellationToken ct = default)
    {
        // Nothing configured is not a failure to scan - it is a server that was never asked to. The
        // reject-on-unscannable switch deliberately does not apply here, or turning it on without an
        // engine would refuse every upload on the server.
        if (this.engines.Count == 0)
            return new ScanDecision(true, ScanVerdict.Skipped, null, "No scanner configured");

        var length = new FileInfo(path).Length;
        if (options.MaxScanBytes > 0 && length > options.MaxScanBytes)
        {
            var why = $"{length} bytes is over the {options.MaxScanBytes} byte scan limit";
            logger.LogInformation("Not scanning {File}: {Why}", displayName, why);
            return new ScanDecision(!options.RejectUnscannable, ScanVerdict.Skipped, null, why);
        }

        var problems = new List<string>();

        foreach (var engine in this.engines)
        {
            var result = await this.Run(engine, path, displayName, ct).ConfigureAwait(false);

            switch (result.Verdict)
            {
                // The first engine to find something ends it: a second opinion on a file that is
                // already going in the bin costs a scan and changes nothing.
                case ScanVerdict.Infected:
                    logger.LogWarning(
                        "{Engine} found {Signature} in {File}", engine.Name, result.Detail, displayName
                    );
                    return new ScanDecision(false, ScanVerdict.Infected, engine.Name, result.Detail);

                case ScanVerdict.Clean:
                    return new ScanDecision(true, ScanVerdict.Clean, engine.Name, null);

                default:
                    problems.Add($"{engine.Name}: {result.Detail}");
                    break;
            }
        }

        // Every engine failed to answer. Whether that stops the upload is the operator's policy.
        var detail = String.Join("; ", problems);
        logger.LogWarning("No engine could scan {File} - {Detail}", displayName, detail);
        return new ScanDecision(!options.RejectUnscannable, ScanVerdict.Unavailable, null, detail);
    }

    public async Task<IReadOnlyList<string>> Describe(CancellationToken ct = default)
    {
        var lines = new List<string>();

        foreach (var engine in this.engines)
        {
            bool available;
            try
            {
                available = await engine.IsAvailable(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lines.Add($"{engine.Name}: unavailable - {ex.Message}");
                continue;
            }

            lines.Add($"{engine.Name}: {(available ? "ready" : "unavailable")}");
        }

        return lines;
    }

    async Task<ScanResult> Run(IFileScanner engine, string path, string displayName, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Timeout);

        try
        {
            return await engine.Scan(path, displayName, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ScanResult.Unavailable($"timed out after {options.Timeout}");
        }
        catch (Exception ex)
        {
            // An engine that throws is an engine that is down. It must never be the thing that
            // takes an upload - or the server - with it.
            logger.LogError(ex, "{Engine} failed scanning {File}", engine.Name, displayName);
            return ScanResult.Unavailable(ex.Message);
        }
    }
}

/// <summary>
/// Thrown from the inspection hook while an upload is still in its temporary file, so the rejected
/// bytes are deleted rather than published. Carries the decision so the endpoint can log and answer.
/// </summary>
public class FileRejectedException(ScanDecision decision, long bytes) : Exception(decision.Describe())
{
    public ScanDecision Decision { get; } = decision;

    /// <summary>How big the refused file was, so the audit entry says what was turned away.</summary>
    public long Bytes { get; } = bytes;
}
