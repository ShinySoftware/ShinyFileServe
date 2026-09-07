using Microsoft.Extensions.Logging;
using nClam;

namespace ShinyFileServe.Core.Services;

public class ClamAvOptions
{
    /// <summary>Where clamd is listening. Local by default - this is not a protocol to expose.</summary>
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 3310;

    /// <summary>
    /// Hand clamd the path and let it open the file itself, instead of streaming the bytes to it.
    /// Faster, and it side-steps clamd's <c>StreamMaxLength</c> - but it only works when clamd runs
    /// on this machine <em>and</em> the account it runs as can read the share folder, which on a
    /// packaged clamav (running as user <c>clamav</c>) it usually cannot. Off by default because the
    /// failure is silent-looking: clamd answers "file not found" rather than "clean".
    /// </summary>
    public bool ScanByPath { get; set; }

    /// <summary>Bytes per INSTREAM chunk. clamd's own default is 128KB.</summary>
    public int MaxChunkSize { get; set; } = 131072;

    /// <summary>
    /// Largest stream clamd will accept, which must match its <c>StreamMaxLength</c> - the default
    /// there is 25MB. Raising <see cref="FileScanOptions.MaxScanBytes"/> past this without raising
    /// both is a scan that always fails.
    /// </summary>
    public long MaxStreamBytes { get; set; } = 25L * 1024 * 1024;
}

/// <summary>
/// Talks to a ClamAV daemon over its socket protocol. Nothing of ClamAV is linked in: clamd is a
/// separate process the operator installs and updates with freshclam, and this only speaks to it -
/// which is also what keeps ClamAV's GPL on its own side of the wire.
/// </summary>
public class ClamAvFileScanner(ClamAvOptions options, ILogger<ClamAvFileScanner> logger) : IFileScanner
{
    public string Name => "ClamAV";

    public async Task<bool> IsAvailable(CancellationToken ct = default)
        => await this.Client().TryPingAsync(ct).ConfigureAwait(false);

    public async Task<ScanResult> Scan(string path, string displayName, CancellationToken ct = default)
    {
        var client = this.Client();

        ClamScanResult result;
        if (options.ScanByPath)
        {
            result = await client.ScanFileOnServerAsync(path, ct).ConfigureAwait(false);
        }
        else
        {
            await using var file = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, options.MaxChunkSize, useAsync: true
            );
            result = await client.SendAndScanFileAsync(file, ct).ConfigureAwait(false);
        }

        switch (result.Result)
        {
            case ClamScanResults.Clean:
                return ScanResult.Clean;

            case ClamScanResults.VirusDetected:
                // clamd answers "stream: <signature> FOUND", and the parsed name keeps the space
                // that separated it from the colon.
                var signature = result.InfectedFiles?.FirstOrDefault()?.VirusName?.Trim();
                if (String.IsNullOrEmpty(signature))
                    signature = "unknown signature";
                return ScanResult.Infected(signature);

            default:
                // Error and Unknown both mean clamd did not give a verdict - a size limit hit, a
                // file it could not open, a daemon mid-reload. Never read as clean.
                logger.LogWarning("clamd gave no verdict for {File}: {Raw}", displayName, result.RawResult);
                return ScanResult.Unavailable(result.RawResult?.Trim() ?? "no verdict");
        }
    }

    ClamClient Client() => new(options.Host, options.Port)
    {
        MaxChunkSize = options.MaxChunkSize,
        MaxStreamSize = options.MaxStreamBytes
    };
}
