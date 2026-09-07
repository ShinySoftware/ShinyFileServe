using Shiny.DocumentDb;
using ShinyFileServe.Core.Documents;

namespace ShinyFileServe.Core.Services;

public interface ISettingsManager
{
    /// <summary>The last value read or written. Never null after <see cref="Load"/>.</summary>
    ServerSettings Current { get; }

    Task<ServerSettings> Load(CancellationToken ct = default);
    Task Save(ServerSettings settings, CancellationToken ct = default);

    /// <summary>Raised after a successful save, on whichever thread did the writing.</summary>
    event EventHandler<ServerSettings>? Changed;
}

public class SettingsManager(IDocumentStore store, TimeProvider time) : ISettingsManager
{
    ServerSettings current = new();

    public ServerSettings Current => this.current;

    public event EventHandler<ServerSettings>? Changed;

    public async Task<ServerSettings> Load(CancellationToken ct = default)
    {
        this.current = await store.Get<ServerSettings>(ServerSettings.SingletonId, cancellationToken: ct)
            ?? new ServerSettings();

        return this.current;
    }

    public async Task Save(ServerSettings settings, CancellationToken ct = default)
    {
        settings.Id = ServerSettings.SingletonId;
        settings.UpdatedUtc = time.GetUtcNow();

        // Upsert rather than insert-or-update: the singleton may or may not have been written yet,
        // and racing the two calls from the desktop app and the web UI is otherwise a real hazard.
        await store.Upsert(settings, cancellationToken: ct);

        this.current = settings;
        this.Changed?.Invoke(this, settings);
    }
}
