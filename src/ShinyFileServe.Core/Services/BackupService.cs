using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Shiny.DocumentDb;
using Shiny.DocumentDb.Sqlite;
using ShinyFileServe.Core.Documents;

namespace ShinyFileServe.Core.Services;

/// <summary>
/// What is inside a backup file, read before anyone is allowed to restore it. A restore replaces
/// every account on the server, so the one thing worth refusing outright is a file with no
/// administrator in it - that is a lock-out, not a restore.
/// </summary>
public record BackupInfo(
    bool IsValid,
    string? Problem,
    int Users,
    int Administrators,
    int Shares,
    int ActivityEntries,
    long SizeBytes,
    DateTimeOffset? TakenUtc
)
{
    public static BackupInfo Invalid(string problem) => new(false, problem, 0, 0, 0, 0, 0, null);
}

public interface IBackupService
{
    /// <summary>
    /// A hot copy of the live database, through SQLite's own backup API - the server keeps serving
    /// while it runs, and the copy is a consistent snapshot rather than a file caught mid-write.
    /// </summary>
    Task<FileInfo> Create(string destinationPath, CancellationToken ct = default);

    /// <summary>Reads a candidate backup without touching the live database.</summary>
    Task<BackupInfo> Inspect(string path, CancellationToken ct = default);

    /// <summary>
    /// Replaces the contents of the live database with <paramref name="path"/>. The caller must
    /// have stopped the server first, so nothing is reading while the pages are replaced.
    /// </summary>
    Task Apply(string path, CancellationToken ct = default);

    /// <summary>Where <see cref="Apply"/> put the previous database, so a bad restore is undoable.</summary>
    string PreviousDatabasePath { get; }
}

public class BackupService(
    SqliteDocumentStore store,
    DocumentStoreOptions storeOptions,
    FileServeOptions options,
    ILogger<BackupService> logger
) : IBackupService
{
    string LivePath => Path.GetFullPath(options.DatabasePath);

    public string PreviousDatabasePath => this.LivePath + ".previous";

    public async Task<FileInfo> Create(string destinationPath, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!String.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(destinationPath))
            File.Delete(destinationPath);

        await store.Backup(destinationPath, ct);
        return new FileInfo(destinationPath);
    }

    public async Task<BackupInfo> Inspect(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return BackupInfo.Invalid("That file is no longer there.");

        var size = new FileInfo(path).Length;

        // Opened through a throwaway copy rather than in place. The provider sets pragmas on every
        // connection, which a read-only connection refuses outright, and opening the staged file
        // read-write would leave a journal beside it that the restore then would not carry across.
        var probePath = path + ".probe";

        try
        {
            File.Copy(path, probePath, overwrite: true);

            var probeOptions = new DocumentStoreOptions
            {
                DatabaseProvider = new SqliteDatabaseProvider($"Data Source={probePath}"),
                JsonSerializerOptions = storeOptions.JsonSerializerOptions,
                UseReflectionFallback = false,
                SkipTableInitialization = true
            };

            using var probe = new SqliteDocumentStore(probeOptions);

            var users = await probe.Query<User>().ToList(ct);
            var admins = users.Count(u => u.IsAdmin || u.IsProtected);

            if (users.Count == 0)
                return BackupInfo.Invalid("That backup has no accounts in it.");

            if (admins == 0)
                return BackupInfo.Invalid("That backup has no administrator in it. Restoring it would lock everyone out.");

            var shares = await probe.Query<ShareFolder>().Count(ct);
            var activity = await probe.Query<ActivityLogEntry>().Count(ct);
            var settings = await probe.Get<ServerSettings>(ServerSettings.SingletonId, cancellationToken: ct);

            return new BackupInfo(true, null, users.Count, admins, (int)shares, (int)activity, size, settings?.UpdatedUtc);
        }
        catch (SqliteException ex)
        {
            // Somebody picking the wrong file is the ordinary case here, not a fault - so the
            // reason is logged without a stack trace nobody is going to read.
            logger.LogInformation("Rejected a backup file SQLite could not open: {Reason}", ex.Message);
            return BackupInfo.Invalid("That file is not a FileServe database.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Rejected a backup file that could not be read");
            return BackupInfo.Invalid("That file could not be read as a FileServe database.");
        }
        finally
        {
            // The probe held connections; release them before the files go, or Windows keeps a lock.
            SqliteConnection.ClearAllPools();

            foreach (var leftover in new[] { probePath, probePath + "-wal", probePath + "-shm" })
            {
                if (File.Exists(leftover))
                    File.Delete(leftover);
            }
        }
    }

    public async Task Apply(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("The backup to restore is no longer there.", path);

        // Kept before anything is overwritten. A restore is the operation most likely to be
        // regretted, and without this there is nothing to regret it back to.
        await this.Create(this.PreviousDatabasePath, ct);

        // Written *into* the live database rather than over the file. The store holds a long-lived
        // connection of its own, so a file swap would leave it reading the replaced inode - the
        // restore would appear to do nothing at all. SQLite's backup API replaces the pages behind
        // that connection instead, which is what it is for.
        await using var source = new SqliteConnection($"Data Source={path};Mode=ReadWrite");
        await source.OpenAsync(ct);

        await using var destination = new SqliteConnection($"Data Source={this.LivePath}");
        await destination.OpenAsync(ct);

        source.BackupDatabase(destination);

        logger.LogWarning(
            "Restored the database from {Path}. The previous one is at {Previous}",
            path,
            this.PreviousDatabasePath
        );
    }
}
