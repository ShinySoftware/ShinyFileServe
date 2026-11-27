using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny;
using Shiny.DocumentDb;
using ShinyFileServe.Core;
using ShinyFileServe.Core.Documents;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Tests.Infrastructure;

/// <summary>The ways a test can talk to the server. HTTP/2 only exists over TLS, where browsers negotiate it.</summary>
public enum Wire
{
    Http11,
    Https11,
    Https2
}

/// <summary>What the share grants the test account, set before the server starts.</summary>
public sealed class HostSettings
{
    public FolderPermissions Permissions { get; set; } =
        FolderPermissions.List | FolderPermissions.DownloadFile | FolderPermissions.CreateFile
        | FolderPermissions.DeleteFile | FolderPermissions.CreateDirectory;

    public long? MaxUploadFileSizeBytes { get; set; }
    public long? DownloadQuotaBytes { get; set; }
    public long? UploadQuotaBytes { get; set; }
    public int? MaxParallelDownloads { get; set; }
    public int? MaxParallelUploads { get; set; }

    /// <summary>
    /// The server's stall limits: how long a response write or a request body read may wait for the
    /// client. Null keeps the server's defaults (60 s and 30 s). Tests about clients that vanish
    /// shorten them so they finish in seconds; the behaviour is the same at any length.
    /// </summary>
    public TimeSpan? StallTimeout { get; set; }
}

/// <summary>
/// The whole FileServe server, as the headless head runs it, on loopback: a real SQLite database, a
/// real share folder, real sockets for plain http and for HTTPS on the server's own self-signed
/// certificate. One account (not an administrator, so grants and quotas apply) with one share.
/// </summary>
public sealed class FileServeHost : IAsyncDisposable
{
    public const string Username = "tester";
    public const string Password = "Correct-Horse-Battery-9!";

    readonly ServiceProvider services;

    FileServeHost(ServiceProvider services, string root, int httpPort, int httpsPort)
    {
        this.services = services;
        this.Root = root;
        this.HttpPort = httpPort;
        this.HttpsPort = httpsPort;
    }

    public string Root { get; }
    public string ShareDirectory => Path.Combine(this.Root, "share");
    public string UploadsDirectory => Path.Combine(this.Root, "uploads");
    public int HttpPort { get; }
    public int HttpsPort { get; }
    public string FolderId { get; private set; } = "";
    public string UserId { get; private set; } = "";

    public IServiceProvider Services => this.services;
    public T Get<T>() where T : notnull => this.services.GetRequiredService<T>();

    public static async Task<FileServeHost> Start(Action<HostSettings>? configure = null)
    {
        var settings = new HostSettings();
        configure?.Invoke(settings);

        var root = Path.Combine(Path.GetTempPath(), "fileserve-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path.Combine(root, "share"));
        var httpPort = FreePort();
        var httpsPort = FreePort();

        var collection = new ServiceCollection();
        collection.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        collection.AddSingleton<IAppStartupInstaller, NoAppStartupInstaller>();
        collection.AddFileServeCore(o => o.DatabasePath = Path.Combine(root, "fileserve.db"));
        collection.AddFileServeServer(o =>
        {
            o.Address = IPAddress.Loopback;
            o.Port = httpPort;
            o.HttpsPort = httpsPort;
            o.TicketKeyPath = Path.Combine(root, "fileserve.key");
            o.CertificatePath = Path.Combine(root, "server.pfx");
            o.DataDirectory = Path.Combine(root, "webapp");
            o.PackagesDirectory = Path.Combine(root, "packages");
            o.UploadsDirectory = Path.Combine(root, "uploads");
            o.TrustLoopback = false;
            o.ShowExceptionDetails = true;
        });

        var provider = collection.BuildServiceProvider();
        var host = new FileServeHost(provider, root, httpPort, httpsPort);

        await provider.GetRequiredService<FileServeInitializer>().Initialize();
        await host.Seed(settings);

        if (settings.StallTimeout is { } stall)
        {
            var limits = provider.GetRequiredService<Shiny.Net.HttpServer.HttpServer>().Options.Limits;
            limits.ResponseWriteTimeout = stall;
            limits.RequestBodyTimeout = stall;
        }

        await provider.GetRequiredService<IFileServeServer>().Start();

        return host;
    }

    async Task Seed(HostSettings settings)
    {
        var users = this.Get<IUserManager>();
        var folders = this.Get<IFolderManager>();

        // An administrator first, so the server is past setup, then the account the tests use.
        await users.CompleteSetup("admin", Password, "Admin");
        var user = await users.CreateUser(Username, Password, "Tester");
        var role = await users.CreateRole("testers");
        user.RoleIds.Add(role.Id);
        user.MaxParallelDownloads = settings.MaxParallelDownloads;
        user.MaxParallelUploads = settings.MaxParallelUploads;
        await users.SaveUser(user);

        var folder = await folders.CreateFolder("Share", this.ShareDirectory);
        await folders.SetGrant(new FolderGrant
        {
            FolderId = folder.Id,
            RoleId = role.Id,
            Permissions = settings.Permissions,
            MaxUploadFileSizeBytes = settings.MaxUploadFileSizeBytes,
            DownloadQuotaWindow = settings.DownloadQuotaBytes is null ? QuotaWindow.None : QuotaWindow.Daily,
            DownloadQuotaBytes = settings.DownloadQuotaBytes,
            UploadQuotaWindow = settings.UploadQuotaBytes is null ? QuotaWindow.None : QuotaWindow.Daily,
            UploadQuotaBytes = settings.UploadQuotaBytes
        });

        this.FolderId = folder.Id;
        this.UserId = user.Id;
    }

    /// <summary>
    /// A signed-in client over <paramref name="wire"/>, with its own cookie jar and its own connections.
    /// <paramref name="via"/> sends it through a proxy instead of straight to the server.
    /// </summary>
    public async Task<TestClient> Client(Wire wire, VanishingProxy? via = null)
    {
        var handler = new SocketsHttpHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
        };

        var baseAddress = wire == Wire.Http11
            ? new Uri($"http://127.0.0.1:{via?.Port ?? this.HttpPort}/")
            : new Uri($"https://127.0.0.1:{via?.Port ?? this.HttpsPort}/");

        var http = new HttpClient(handler)
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromMinutes(2),
            DefaultRequestVersion = wire == Wire.Https2 ? HttpVersion.Version20 : HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        var login = await http.PostAsJsonAsync("api/auth/login", new { username = Username, password = Password });
        if (!login.IsSuccessStatusCode)
            throw new InvalidOperationException($"Sign-in failed: {(int)login.StatusCode} {await login.Content.ReadAsStringAsync()}");

        var expected = wire == Wire.Https2 ? HttpVersion.Version20 : HttpVersion.Version11;
        if (login.Version != expected)
            throw new InvalidOperationException($"Expected HTTP/{expected} but the server answered HTTP/{login.Version}");

        return new TestClient(http, this);
    }

    /// <summary>Writes a file of random bytes into the share and returns them.</summary>
    public byte[] PutFile(string path, int length, int seed = 1)
    {
        var bytes = Data.Random(length, seed);
        var full = Path.Combine(this.ShareDirectory, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return bytes;
    }

    public string SharePath(string path) => Path.Combine(this.ShareDirectory, path);

    /// <summary>Anything a failed save might have left behind in the share: the temp files saves write first.</summary>
    public IReadOnlyList<string> StrayTempFiles()
        => Directory.EnumerateFiles(this.ShareDirectory, "*.uploading-*", SearchOption.AllDirectories).ToList();

    /// <summary>Bytes left in the resumable upload store.</summary>
    public IReadOnlyList<string> PendingUploadFiles()
        => Directory.Exists(this.UploadsDirectory)
            ? Directory.EnumerateFiles(this.UploadsDirectory).ToList()
            : [];

    public async Task<int> ReservationCount()
        => (await this.Get<IDocumentStore>().Query<ResumableUpload>().ToList()).Count;

    public async Task<long> DownloadedBytesToday()
        => (await this.Get<IQuotaTracker>().GetUsage(this.UserId, this.FolderId, QuotaWindow.Daily)).DownloadedBytes;

    public async Task<long> UploadedBytesToday()
        => (await this.Get<IQuotaTracker>().GetUsage(this.UserId, this.FolderId, QuotaWindow.Daily)).UploadedBytes;

    static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await this.Get<IFileServeServer>().Stop();
        }
        catch (ObjectDisposedException)
        {
        }

        await this.services.DisposeAsync();

        try
        {
            Directory.Delete(this.Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    sealed class NoAppStartupInstaller : IAppStartupInstaller
    {
        public bool IsSupported => false;
        public Task<AppStartupState> GetState(CancellationToken cancellationToken = default) => Task.FromResult(AppStartupState.NotSupported);
        public Task<AppStartupState> Register(CancellationToken cancellationToken = default) => Task.FromResult(AppStartupState.NotSupported);
        public Task<AppStartupState> Unregister(CancellationToken cancellationToken = default) => Task.FromResult(AppStartupState.NotSupported);
        public Task<bool> OpenSettings() => Task.FromResult(false);
    }
}
