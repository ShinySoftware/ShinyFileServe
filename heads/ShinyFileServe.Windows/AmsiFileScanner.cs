using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShinyFileServe.Core;
using ShinyFileServe.Core.Services;

namespace ShinyFileServe.Windows;

/// <summary>
/// Scans a finished upload through the Antimalware Scan Interface, which hands the bytes to whatever
/// antivirus the machine already runs - Defender on a stock Windows, whichever product replaced it
/// otherwise. Nothing to install and nothing to keep updated: the engine and its signatures are the
/// operator's, already there and already current.
/// </summary>
/// <remarks>
/// AMSI wants the whole file in one buffer - it has no chunked form that would still match a
/// signature straddling a boundary - so this is bounded by <see cref="FileScanOptions.MaxScanBytes"/>
/// and refuses politely above <see cref="MaxBytes"/> rather than allocating something absurd.
/// </remarks>
public sealed partial class AmsiFileScanner(ILogger<AmsiFileScanner> logger) : IFileScanner, IDisposable
{
    /// <summary>A result at or above this is malware; below it is a clean or "not detected" grade.</summary>
    const int DetectionThreshold = 32;

    /// <summary>The hard ceiling on a single buffer, whatever the scan options say.</summary>
    public const long MaxBytes = 256L * 1024 * 1024;

    readonly Lock gate = new();
    IntPtr context;
    bool initialised;
    bool broken;

    public string Name => "AMSI";

    public Task<bool> IsAvailable(CancellationToken ct = default) => Task.FromResult(this.TryInitialise());

    public async Task<ScanResult> Scan(string path, string displayName, CancellationToken ct = default)
    {
        if (!this.TryInitialise())
            return ScanResult.Unavailable("no AMSI provider is registered on this machine");

        var length = new FileInfo(path).Length;
        if (length == 0)
            return ScanResult.Clean;

        if (length > MaxBytes)
            return ScanResult.Unavailable($"{length} bytes is more than AMSI will take in one buffer");

        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);

        var session = IntPtr.Zero;
        var opened = AmsiOpenSession(this.context, out session);
        if (opened < 0)
            return ScanResult.Unavailable($"AmsiOpenSession failed (0x{opened:x8})");

        // Pinned rather than copied to unmanaged memory: AMSI reads the buffer and returns, so the
        // pin lasts exactly as long as the call.
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            // The name the file will have, not the temp path it currently sits at - a provider uses
            // it for its own heuristics and its logs, and the temp name means nothing to either.
            var hr = AmsiScanBuffer(
                this.context, pin.AddrOfPinnedObject(), (uint)bytes.Length, displayName, session, out var result
            );

            if (hr < 0)
                return ScanResult.Unavailable($"AmsiScanBuffer failed (0x{hr:x8})");

            return result >= DetectionThreshold
                ? ScanResult.Infected($"flagged by the AMSI provider (result {result})")
                : ScanResult.Clean;
        }
        finally
        {
            pin.Free();
            AmsiCloseSession(this.context, session);
        }
    }

    /// <summary>
    /// One context for the life of the process, as AMSI intends - initialising per file makes every
    /// upload pay for loading the provider. A failure here is remembered: if amsi.dll is not there
    /// on the first attempt it will not appear on the second.
    /// </summary>
    bool TryInitialise()
    {
        lock (this.gate)
        {
            if (this.initialised)
                return !this.broken;

            this.initialised = true;

            try
            {
                var hr = AmsiInitialize("ShinyFileServe", out this.context);
                this.broken = hr < 0 || this.context == IntPtr.Zero;

                if (this.broken)
                    logger.LogWarning("AmsiInitialize failed (0x{Result:x8}); uploads will not be scanned by AMSI", hr);
            }
            catch (DllNotFoundException ex)
            {
                this.broken = true;
                logger.LogWarning(ex, "amsi.dll is not available; uploads will not be scanned by AMSI");
            }

            return !this.broken;
        }
    }

    public void Dispose()
    {
        lock (this.gate)
        {
            if (this.context != IntPtr.Zero)
            {
                AmsiUninitialize(this.context);
                this.context = IntPtr.Zero;
            }

            this.broken = true;
            this.initialised = true;
        }
    }

    [LibraryImport("amsi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int AmsiInitialize(string appName, out IntPtr amsiContext);

    [LibraryImport("amsi.dll")]
    private static partial void AmsiUninitialize(IntPtr amsiContext);

    [LibraryImport("amsi.dll")]
    private static partial int AmsiOpenSession(IntPtr amsiContext, out IntPtr session);

    [LibraryImport("amsi.dll")]
    private static partial void AmsiCloseSession(IntPtr amsiContext, IntPtr session);

    [LibraryImport("amsi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int AmsiScanBuffer(
        IntPtr amsiContext, IntPtr buffer, uint length, string contentName, IntPtr session, out int result
    );
}

public static class AmsiRegistration
{
    /// <summary>
    /// Adds AMSI scanning of uploads. Registered by the Windows head by default: it costs nothing to
    /// have and asks the operator for nothing, which is not true of any engine we would ship.
    /// </summary>
    public static IServiceCollection AddAmsiScanner(this IServiceCollection services)
    {
        services.AddFileScanning();
        services.AddSingleton<AmsiFileScanner>();
        services.AddSingleton<IFileScanner>(sp => sp.GetRequiredService<AmsiFileScanner>());
        return services;
    }
}
