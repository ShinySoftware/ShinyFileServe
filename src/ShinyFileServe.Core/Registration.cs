using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shiny.DocumentDb;
using Shiny.DocumentDb.Sqlite;
using ShinyFileServe.Core.Documents;
using ShinyFileServe.Core.Services;

namespace ShinyFileServe.Core;

public static class Registration
{
    /// <summary>
    /// Registers the SQLite-backed document store plus every domain service. Safe to call from a MAUI
    /// host, a console host, or a test fixture - nothing here touches a UI or a platform API.
    /// </summary>
    public static IServiceCollection AddFileServeCore(this IServiceCollection services, Action<FileServeOptions> configure)
    {
        var options = new FileServeOptions();
        configure(options);
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

        var json = new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = CoreJsonContext.Default
        };

        // Registered as the concrete SqliteDocumentStore rather than through AddDocumentStore,
        // which builds a plain DocumentStore. The file-level hot backup is a SQLite capability and
        // lives on the concrete type; everything else still resolves IDocumentStore.
        var storeOptions = new DocumentStoreOptions
        {
            DatabaseProvider = new SqliteDatabaseProvider($"Data Source={options.DatabasePath}"),
            JsonSerializerOptions = json,
            UseReflectionFallback = false
        };
        storeOptions.UseGuidV7Ids();

        services.AddSingleton(storeOptions);
        services.AddSingleton(_ => new SqliteDocumentStore(storeOptions));
        services.AddSingleton<IDocumentStore>(sp => sp.GetRequiredService<SqliteDocumentStore>());

        services.AddSingleton<IUserManager, UserManager>();
        services.AddSingleton<IAccessTokenManager, AccessTokenManager>();
        services.AddSingleton<IFolderManager, FolderManager>();
        services.AddSingleton<IQuotaTracker, QuotaTracker>();
        services.AddSingleton<IAccessResolver, AccessResolver>();
        services.AddSingleton<IActivityLogger, ActivityLogger>();
        services.AddSingleton<IBandwidthMeter, BandwidthMeter>();
        services.AddSingleton<IPackageTrafficLog, PackageTrafficLog>();
        services.AddSingleton<ISettingsManager, SettingsManager>();
        services.AddSingleton<IPasswordPolicy, PasswordPolicy>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<IFileService, FileService>();
        services.AddSingleton<FileServeInitializer>();

        // Always resolvable, with no engines behind it until a head adds one - an unscanned server
        // is then the same code path as a scanned one, and every file comes back skipped.
        services.AddFileScanning();

        return services;
    }

    /// <summary>
    /// Registers the policy that runs over a finished upload. Engines are added separately with
    /// <see cref="AddClamAvScanner"/> or by a head registering its own <see cref="IFileScanner"/>.
    /// </summary>
    public static IServiceCollection AddFileScanning(this IServiceCollection services, Action<FileScanOptions>? configure = null)
    {
        if (configure is not null)
        {
            var options = new FileScanOptions();
            configure(options);
            services.RemoveAll<FileScanOptions>();
            services.AddSingleton(options);
        }
        else
        {
            services.TryAddSingleton<FileScanOptions>();
        }

        services.TryAddSingleton<IUploadScanner, UploadScanner>();
        return services;
    }

    /// <summary>
    /// Scans uploads with a ClamAV daemon. clamd is not shipped with the app: the operator installs
    /// it and keeps its signatures current with freshclam, and this only speaks to its socket.
    /// </summary>
    public static IServiceCollection AddClamAvScanner(this IServiceCollection services, Action<ClamAvOptions>? configure = null)
    {
        var options = new ClamAvOptions();
        configure?.Invoke(options);

        services.AddFileScanning();
        services.AddSingleton(options);
        services.AddSingleton<IFileScanner, ClamAvFileScanner>();
        return services;
    }
}

/// <summary>
/// First-run work: create the bootstrap admin when there are no users at all, and prune the activity
/// log. Call it once from the host before starting the server.
/// </summary>
public class FileServeInitializer(
    IUserManager users,
    IActivityLogger activity,
    IBandwidthMeter bandwidth,
    IPackageTrafficLog packageTraffic,
    ISettingsManager settings,
    IUploadScanner scanner,
    FileServeOptions options,
    TimeProvider time,
    ILogger<FileServeInitializer> logger
)
{
    public async Task Initialize(CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath));
        if (!String.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await settings.Load(ct);
        await this.MigrateDisabledAccounts(ct);

        // Nothing is seeded. An account with a password the whole internet knows is worse than no
        // account at all, so the first person to open the site creates the administrator.
        if (await users.NeedsSetup(ct))
            logger.LogInformation("No accounts yet. Open the server in a browser to create the administrator.");

        // Asked once, at startup, rather than discovered on the first upload of the day: a scanner
        // that is configured but not answering is something an operator wants to hear about before
        // anyone has trusted it with a file.
        if (scanner.IsEnabled)
        {
            foreach (var line in await scanner.Describe(ct))
                logger.LogInformation("Virus scanning - {Engine}", line);
        }
        else
        {
            logger.LogInformation("Virus scanning is off: no engine is configured");
        }

        if (options.ActivityRetention is { } retention)
        {
            var pruned = await activity.Prune(retention, ct);
            if (pruned > 0)
                logger.LogInformation("Pruned {Count} activity entries older than {Retention}", pruned, retention);
        }

        // Kept separately from the log it sits beside: one row an hour per account is small enough
        // to keep for years, where a line per action is not, and an operator who trims the log to a
        // month should not lose last quarter's traffic with it.
        if (options.BandwidthRetention is { } bandwidthRetention)
        {
            var pruned = await bandwidth.Prune(bandwidthRetention, ct);
            if (pruned > 0)
                logger.LogInformation("Pruned {Count} bandwidth rows older than {Retention}", pruned, bandwidthRetention);
        }

        if (options.PackageTrafficRetention is { } trafficRetention)
        {
            var pruned = await packageTraffic.Prune(trafficRetention, ct);
            if (pruned > 0)
                logger.LogInformation("Pruned {Count} package feed requests older than {Retention}", pruned, trafficRetention);
        }
    }

    /// <summary>
    /// Accounts used to be switched off with an <c>isActive</c> flag; they now carry an expiry date
    /// instead. A database written before the change still holds the old field, and dropping it
    /// would hand every disabled account back its access on the first start after an upgrade - so
    /// a stored false becomes an expiry of now, which is the same account in the new vocabulary.
    /// The flag is cleared as it is read, so this runs once and then finds nothing.
    /// </summary>
    async Task MigrateDisabledAccounts(CancellationToken ct)
    {
        var stale = (await users.GetUsers(ct)).Where(u => u.LegacyIsActive is not null).ToList();
        if (stale.Count == 0)
            return;

        var now = time.GetUtcNow();
        var disabled = 0;

        foreach (var user in stale)
        {
            // Only a stored false says anything; true was the default and means nothing now.
            if (user.LegacyIsActive is false && user.ExpiresAtUtc is null && !user.IsProtected)
            {
                user.ExpiresAtUtc = now;
                disabled++;
            }

            user.LegacyIsActive = null;
            await users.SaveUser(user, ct);
        }

        logger.LogInformation(
            "Migrated {Count} accounts to expiry dates, {Disabled} of which were disabled and are now expired",
            stale.Count, disabled
        );
    }
}
