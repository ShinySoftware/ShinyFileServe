using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Markdig;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server.Updates;
using ShinyFileServe.Shared;

namespace ShinyFileServe.App.Services;

/// <summary>
/// New releases of the app, from the GitHub releases the release workflow publishes. Checked once
/// when the app starts, and again whenever an administrator asks from Settings.
/// </summary>
/// <remarks>
/// <para>
/// Only macOS installs itself, because macOS is the only build the releases carry. Everywhere else
/// the release is still found and shown, and the way to it is its page on GitHub.
/// </para>
/// <para>
/// A download is trusted for the same reasons Gatekeeper would trust it, checked before anything is
/// replaced: the bundle's signature is intact, it is signed by the same team as the copy running
/// now, and it is notarized. The running app is never touched until all three hold, and the swap
/// itself happens after the app has quit, by a script that puts the old copy back if it cannot.
/// </para>
/// <para>
/// FILESERVE_UPDATES=0 turns checking off and =1 turns it on in a debug build, where it is off by
/// default - a build off a developer's desk is always older than the last release, and asking about
/// it on every run would only be noise.
/// </para>
/// </remarks>
public class AppUpdater(
    ISettingsManager settings,
    ServerController server,
    IServiceProvider services,
    ILogger<AppUpdater> logger
) : IAppUpdater
{
    const string Owner = "ShinySoftware";
    const string Repository = "ShinyFileServe";

    static readonly HttpClient Http = CreateClient();
    static readonly MarkdownPipeline Notes = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        // Release notes are written by whoever can push a tag, and they are rendered inside a page
        // that is signed in as the administrator. Markdown, yes; their HTML, no.
        .DisableHtml()
        .Build();

    readonly Lock sync = new();
    readonly ReleaseVersion current = ReleaseVersion.Parse(RunningVersion()) ?? new ReleaseVersion(new Version(0, 0), null);

    Release? latest;
    AppUpdateState state = AppUpdateState.Idle;
    int? progress;
    string? error;
    DateTimeOffset? checkedUtc;
    string? cannotInstall;
    bool prompted;
    string? teamId;

    public event EventHandler? Changed;

    public static bool IsEnabled
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("FILESERVE_UPDATES");
            if (value is "0" || "false".Equals(value, StringComparison.OrdinalIgnoreCase) || "off".Equals(value, StringComparison.OrdinalIgnoreCase))
                return false;
#if DEBUG
            return value is "1" || "true".Equals(value, StringComparison.OrdinalIgnoreCase) || "on".Equals(value, StringComparison.OrdinalIgnoreCase);
#else
            return true;
#endif
        }
    }

    public AppUpdateInfo Current
    {
        get
        {
            lock (this.sync)
            {
                var release = this.latest;
                var available = release is not null;
                var asset = release?.Asset;

                return new AppUpdateInfo(
                    this.current.ToString(),
                    this.state,
                    available,
                    release?.Version.ToString(),
                    release?.Title,
                    release?.NotesHtml,
                    release?.Url,
                    release?.PublishedUtc,
                    asset?.Size,
                    available && this.cannotInstall is null,
                    available ? this.cannotInstall : null,
                    available && String.Equals(settings.Current.SkippedUpdateVersion, release!.Version.ToString(), StringComparison.OrdinalIgnoreCase),
                    this.state == AppUpdateState.Downloading ? this.progress : null,
                    this.error,
                    this.checkedUtc
                );
            }
        }
    }

    /// <summary>
    /// The startup check. After the server has initialised, because whether a release was skipped
    /// is in the settings it loads - asking before then would prompt about one already dismissed.
    /// </summary>
    public void Start()
    {
        if (!IsEnabled)
            return;

        _ = Task.Run(async () =>
        {
            await server.Initialize();
            await this.Check();
        });
    }

    /// <summary>
    /// True once per launch, the first time there is a release on offer that nobody said "Don't show
    /// again" to. The console window asks, and opens the update screen when it is told yes.
    /// </summary>
    public bool TakePrompt()
    {
        var info = this.Current;
        lock (this.sync)
        {
            if (this.prompted || !info.IsAvailable || info.IsSkipped || info.State != AppUpdateState.Checked)
                return false;

            this.prompted = true;
            return true;
        }
    }

    public async Task<AppUpdateInfo> Check(CancellationToken ct = default)
    {
        lock (this.sync)
        {
            if (this.IsInstalling)
                return this.Current;

            this.state = AppUpdateState.Checking;
            this.error = null;
        }
        this.Raise();

        try
        {
            var found = await this.FindNewest(ct);
            var reason = found is null ? null : await this.WhyCannotInstall(found);

            lock (this.sync)
            {
                this.latest = found;
                this.cannotInstall = reason;
                this.state = AppUpdateState.Checked;
                this.checkedUtc = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception ex)
        {
            // Offline, rate limited, GitHub having a day. The app works exactly as well without the
            // answer, so it is reported on the settings screen and nowhere louder.
            logger.LogWarning(ex, "Could not check GitHub for a new release");
            lock (this.sync)
            {
                this.state = AppUpdateState.Failed;
                this.error = ex is HttpRequestException or TaskCanceledException
                    ? "GitHub could not be reached."
                    : ex.Message;
            }
        }

        this.Raise();
        return this.Current;
    }

    public async Task<AppUpdateInfo> Skip(bool skip, CancellationToken ct = default)
    {
        var version = this.latest?.Version.ToString();
        var stored = await settings.Load(ct);
        // Cleared with an empty string, not null: the upsert leaves a null property out of the
        // document, so the version said no to before would quietly survive being un-ticked.
        stored.SkippedUpdateVersion = skip ? version : "";
        await settings.Save(stored, ct);

        this.Raise();
        return this.Current;
    }

    public AppUpdateInfo? BeginInstall()
    {
        Release release;
        lock (this.sync)
        {
            if (this.IsInstalling)
                return this.Current;

            if (this.latest is not { Asset: not null } found || this.cannotInstall is not null)
                return null;

            release = found;
            this.state = AppUpdateState.Downloading;
            this.progress = 0;
            this.error = null;
        }

        this.Raise();
        _ = Task.Run(() => this.Install(release));
        return this.Current;
    }

    bool IsInstalling => this.state is AppUpdateState.Downloading or AppUpdateState.Verifying or AppUpdateState.Restarting;

    // -- finding a release ---------------------------------------------------

    async Task<Release?> FindNewest(CancellationToken ct)
    {
        // The list rather than /releases/latest: "latest" never names a prerelease, and somebody
        // running a beta is owed the next beta. Somebody running a release is only offered releases.
        var url = $"https://api.github.com/repos/{Owner}/{Repository}/releases?per_page=20";
        using var response = await Http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);

        Release? best = null;
        foreach (var item in json.RootElement.EnumerateArray())
        {
            if (Bool(item, "draft"))
                continue;

            if (Bool(item, "prerelease") && !this.current.IsPrerelease)
                continue;

            if (ReleaseVersion.Parse(Text(item, "tag_name")) is not { } version || version.CompareTo(this.current) <= 0)
                continue;

            if (best is not null && version.CompareTo(best.Version) <= 0)
                continue;

            best = new Release(
                version,
                Text(item, "name") is { Length: > 0 } name ? name : $"FileServe {version}",
                Text(item, "body") is { Length: > 0 } notes ? Markdown.ToHtml(notes, Notes) : null,
                Text(item, "html_url"),
                item.TryGetProperty("published_at", out var published) && published.TryGetDateTimeOffset(out var when) ? when : null,
                FindAsset(item)
            );
        }

        return best;
    }

    /// <summary>The download for this machine, by the name the release workflow gives it.</summary>
    static ReleaseAsset? FindAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets))
            return null;

        var suffix = $"-{PlatformTag}.zip";
        foreach (var asset in assets.EnumerateArray())
        {
            if (Text(asset, "name") is { } name
                && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && Text(asset, "browser_download_url") is { } download)
            {
                return new ReleaseAsset(name, download, asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0);
            }
        }

        return null;
    }

    static string PlatformTag
    {
        get
        {
            var os = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux";
            var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            return $"{os}-{arch}";
        }
    }

    /// <summary>Null when this copy can replace itself with <paramref name="release"/>, otherwise why not.</summary>
    async Task<string?> WhyCannotInstall(Release release)
    {
        if (!OperatingSystem.IsMacOS())
            return "This version cannot install updates itself. Download the new one from GitHub.";

        if (release.Asset is null)
            return "That release has no build for this Mac. Download it from GitHub.";

        if (BundlePath() is not { } bundle)
            return "FileServe is not running from an app bundle, so there is nothing to replace.";

        if (!CanWriteBeside(bundle))
            return $"FileServe cannot replace itself in {Path.GetDirectoryName(bundle)}. Move it to Applications, or download the update from GitHub.";

        this.teamId ??= await TeamId(bundle);
        if (this.teamId is null)
            return "This copy of FileServe is not a signed release, so it cannot vouch for a download. Download the update from GitHub.";

        return null;
    }

    // -- installing ----------------------------------------------------------

    async Task Install(Release release)
    {
        var work = Path.Combine(Path.GetTempPath(), "fileserve-update", release.Version.ToString());

        try
        {
            if (Directory.Exists(work))
                Directory.Delete(work, true);
            Directory.CreateDirectory(work);

            var zip = Path.Combine(work, release.Asset!.Name);
            await this.Download(release.Asset, zip);

            this.Move(AppUpdateState.Verifying);
            var bundle = BundlePath()!;
            var staged = await Unpack(zip, Path.Combine(work, "unpacked"));
            await this.Verify(staged, release);

            this.Move(AppUpdateState.Restarting);
            Swap(bundle, staged, Path.Combine(work, "previous"));

            // Long enough for the page that asked to see that it is restarting rather than dropping
            // mid-download, then the listener goes first so the port is free for the new copy.
            await Task.Delay(1500);
            await server.StopServer();

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
                services.GetRequiredService<TrayController>().Quit();
            else
                dispatcher.Dispatch(services.GetRequiredService<TrayController>().Quit);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not install FileServe {Version}", release.Version);
            lock (this.sync)
            {
                this.state = AppUpdateState.Failed;
                this.error = ex is HttpRequestException or TaskCanceledException
                    ? "The download did not finish. Try again."
                    : ex.Message;
            }
            this.Raise();
        }
    }

    async Task Download(ReleaseAsset asset, string path)
    {
        using var response = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? asset.Size;
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(path);

        var buffer = new byte[81920];
        long done = 0;
        var reported = -1;
        int read;

        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read));
            done += read;

            // Only when the whole percentage moves - every 80KB would be thousands of events nobody
            // is reading more than once a second.
            var percent = total > 0 ? (int)(done * 100 / total) : 0;
            if (percent != reported)
            {
                reported = percent;
                lock (this.sync)
                    this.progress = percent;
                this.Raise();
            }
        }
    }

    /// <summary>
    /// ditto rather than ZipFile: an app bundle carries symlinks, executable bits and extended
    /// attributes that the signature covers, and ZipFile drops them - which would hand the checks
    /// below a broken signature on a perfectly good download.
    /// </summary>
    static async Task<string> Unpack(string zip, string into)
    {
        Directory.CreateDirectory(into);
        await Run("/usr/bin/ditto", "-x", "-k", zip, into).Expect("The download could not be unpacked");

        return Directory.GetDirectories(into, "*.app").SingleOrDefault()
            ?? throw new InvalidOperationException("The download did not contain an app.");
    }

    async Task Verify(string staged, Release release)
    {
        await Run("/usr/bin/codesign", "--verify", "--deep", "--strict", staged)
            .Expect("The download's signature is broken");

        var team = await TeamId(staged);
        if (team is null || !String.Equals(team, this.teamId, StringComparison.Ordinal))
            throw new InvalidOperationException("The download is not signed by the same developer as this copy of FileServe.");

        await Run("/usr/sbin/spctl", "--assess", "--type", "execute", staged)
            .Expect("macOS does not trust the download");

        // A release whose bundle says it is something else is a release somebody mislabelled, and
        // installing it could quietly move a machine backwards.
        var plist = Path.Combine(staged, "Contents", "Info.plist");
        var stamped = (await Run("/usr/bin/plutil", "-extract", "CFBundleShortVersionString", "raw", plist)).Output.Trim();
        if (ReleaseVersion.Parse(stamped) is not { } inside || inside.Numbers != release.Version.Numbers)
            throw new InvalidOperationException($"The download says it is version {stamped}, not {release.Version}.");
    }

    /// <summary>
    /// Hands the swap to a shell that outlives this process. It waits for the app to be gone, moves
    /// the old bundle aside, moves the new one in - or the old one back if that fails - and opens
    /// whichever is there, so a failure still ends with FileServe running.
    /// </summary>
    static void Swap(string bundle, string staged, string previous)
    {
        const string script = """
            pid="$1"; target="$2"; staged="$3"; previous="$4"
            while kill -0 "$pid" 2>/dev/null; do sleep 0.5; done
            rm -rf "$previous"
            if mv "$target" "$previous"; then
                if mv "$staged" "$target"; then
                    rm -rf "$previous"
                else
                    mv "$previous" "$target"
                fi
            fi
            open "$target"
            """;

        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        foreach (var argument in new[] { "-c", script, "fileserve-update", Environment.ProcessId.ToString(), bundle, staged, previous })
            start.ArgumentList.Add(argument);

        Process.Start(start)?.Dispose();
    }

    // -- the machine ---------------------------------------------------------

    /// <summary>The .app this process is running from - Contents/MacOS/FileServe, three levels in.</summary>
    static string? BundlePath()
    {
        if (Environment.ProcessPath is not { } executable)
            return null;

        var bundle = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(executable)));
        return bundle is not null && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? bundle : null;
    }

    /// <summary>
    /// The swap needs to move the bundle, which is a write to the folder holding it. Also false for
    /// an app macOS has translocated - run from Downloads without being moved - which runs from a
    /// read-only copy nobody could replace.
    /// </summary>
    static bool CanWriteBeside(string bundle)
    {
        try
        {
            var probe = Path.Combine(Path.GetDirectoryName(bundle)!, $".fileserve-update-{Guid.NewGuid():n}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The Developer ID team a bundle is signed by, or null for ad-hoc and unsigned builds.</summary>
    static async Task<string?> TeamId(string bundle)
    {
        var result = await Run("/usr/bin/codesign", "-dv", "--verbose=2", bundle);
        if (result.ExitCode != 0)
            return null;

        var line = result.Output
            .Split('\n')
            .FirstOrDefault(x => x.StartsWith("TeamIdentifier=", StringComparison.Ordinal));

        var team = line?["TeamIdentifier=".Length..].Trim();
        return team is { Length: > 0 } and not "not set" ? team : null;
    }

    static async Task<ToolResult> Run(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not run {file}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        // codesign -d writes what it found to stderr, so both streams are the answer.
        return new ToolResult(process.ExitCode, await output + await errors);
    }

    /// <summary>
    /// The version stamped into this build - the release tag, passed as -p:Version by the release
    /// workflow. Without the +commit suffix the SDK appends.
    /// </summary>
    static string RunningVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppUpdater).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0";

        return version.Split('+')[0];
    }

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        // GitHub refuses API calls without one.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FileServe", RunningVersion()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static bool Bool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    void Move(AppUpdateState next)
    {
        lock (this.sync)
            this.state = next;
        this.Raise();
    }

    void Raise() => this.Changed?.Invoke(this, EventArgs.Empty);

    record Release(ReleaseVersion Version, string Title, string? NotesHtml, string? Url, DateTimeOffset? PublishedUtc, ReleaseAsset? Asset);

    record ReleaseAsset(string Name, string Url, long Size);
}

readonly record struct ToolResult(int ExitCode, string Output);

static class ToolResultExtensions
{
    public static async Task Expect(this Task<ToolResult> running, string problem)
    {
        var result = await running;
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{problem}: {result.Output.Trim()}");
    }
}

/// <summary>
/// A release tag as a version: 1.2.0, v1.2.0 or 1.2.0-beta1. A prerelease sorts before the release
/// of the same number, which is the one ordering System.Version cannot express on its own.
/// </summary>
public readonly record struct ReleaseVersion(Version Numbers, string? Suffix) : IComparable<ReleaseVersion>
{
    public bool IsPrerelease => this.Suffix is not null;

    public static ReleaseVersion? Parse(string? text)
    {
        if (String.IsNullOrWhiteSpace(text))
            return null;

        var value = text.Trim().TrimStart('v', 'V').Split('+')[0];
        var dash = value.IndexOf('-');
        var numbers = dash < 0 ? value : value[..dash];
        var suffix = dash < 0 ? null : value[(dash + 1)..];

        // System.Version wants at least two parts; a tag of "2" is still a version.
        if (!numbers.Contains('.'))
            numbers += ".0";

        if (!Version.TryParse(numbers, out var parsed))
            return null;

        // 1.2 and 1.2.0 are the same release. Version disagrees, because an unset part is -1.
        parsed = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        return new ReleaseVersion(parsed, String.IsNullOrEmpty(suffix) ? null : suffix);
    }

    public int CompareTo(ReleaseVersion other)
    {
        var numbers = this.Numbers.CompareTo(other.Numbers);
        if (numbers != 0)
            return numbers;

        return (this.Suffix, other.Suffix) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => String.Compare(this.Suffix, other.Suffix, StringComparison.OrdinalIgnoreCase)
        };
    }

    public override string ToString() => this.Suffix is null
        ? this.Numbers.ToString(3)
        : $"{this.Numbers.ToString(3)}-{this.Suffix}";
}
