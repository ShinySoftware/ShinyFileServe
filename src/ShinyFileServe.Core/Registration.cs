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

        services.AddDocumentStore(o =>
        {
            o.DatabaseProvider = new SqliteDatabaseProvider($"Data Source={options.DatabasePath}");
            o.JsonSerializerOptions = json;
            o.UseReflectionFallback = false;
            o.UseGuidV7Ids();
        });

        services.AddSingleton<IUserManager, UserManager>();
        services.AddSingleton<IFolderManager, FolderManager>();
        services.AddSingleton<IQuotaTracker, QuotaTracker>();
        services.AddSingleton<IAccessResolver, AccessResolver>();
        services.AddSingleton<IActivityLogger, ActivityLogger>();
        services.AddSingleton<ISettingsManager, SettingsManager>();
        services.AddSingleton<IFileService, FileService>();
        services.AddSingleton<FileServeInitializer>();

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
    ISettingsManager settings,
    FileServeOptions options,
    ILogger<FileServeInitializer> logger
)
{
    public async Task Initialize(CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath));
        if (!String.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await settings.Load(ct);

        // Nothing is seeded. An account with a password the whole internet knows is worse than no
        // account at all, so the first person to open the site creates the administrator.
        if (await users.NeedsSetup(ct))
            logger.LogInformation("No accounts yet. Open the server in a browser to create the administrator.");

        if (options.ActivityRetention is { } retention)
        {
            var pruned = await activity.Prune(retention, ct);
            if (pruned > 0)
                logger.LogInformation("Pruned {Count} activity entries older than {Retention}", pruned, retention);
        }
    }
}
