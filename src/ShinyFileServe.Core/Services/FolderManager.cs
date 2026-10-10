using Shiny.DocumentDb;
using ShinyFileServe.Core.Documents;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Core.Services;

public interface IFolderManager
{
    Task<IReadOnlyList<ShareFolder>> GetFolders(CancellationToken ct = default);
    Task<ShareFolder?> GetFolder(string id, CancellationToken ct = default);
    Task<ShareFolder> CreateFolder(string name, string physicalPath, string? description = null, CancellationToken ct = default);
    Task SaveFolder(ShareFolder folder, CancellationToken ct = default);
    Task DeleteFolder(string id, CancellationToken ct = default);

    Task<IReadOnlyList<FolderGrant>> GetGrants(CancellationToken ct = default);
    Task<IReadOnlyList<FolderGrant>> GetGrantsForFolder(string folderId, CancellationToken ct = default);
    Task<FolderGrant> SetGrant(FolderGrant grant, CancellationToken ct = default);
    Task DeleteGrant(string id, CancellationToken ct = default);

    /// <summary>Every annotation in one share, keyed by the path it applies to.</summary>
    Task<IReadOnlyDictionary<string, PathAnnotation>> GetAnnotations(string folderId, CancellationToken ct = default);

    /// <summary>Writes an annotation, or removes it when both the name and note are blank.</summary>
    Task SetAnnotation(string folderId, string path, string? displayName, string? description, CancellationToken ct = default);
}

public class FolderManager(IDocumentStore store, TimeProvider time) : IFolderManager
{
    public Task<IReadOnlyList<ShareFolder>> GetFolders(CancellationToken ct = default)
        => store.Query<ShareFolder>().OrderBy(x => x.Name).ToList(ct);

    public Task<ShareFolder?> GetFolder(string id, CancellationToken ct = default)
        => store.Get<ShareFolder>(id, cancellationToken: ct);

    public async Task<ShareFolder> CreateFolder(string name, string physicalPath, string? description = null, CancellationToken ct = default)
    {
        var folder = new ShareFolder
        {
            Id = Ids.New(),
            Name = name.Trim(),
            PhysicalPath = Path.GetFullPath(physicalPath),
            Description = description,
            IsEnabled = true,
            CreatedUtc = time.GetUtcNow()
        };
        await store.Insert(folder, cancellationToken: ct);
        return folder;
    }

    public Task SaveFolder(ShareFolder folder, CancellationToken ct = default)
        => store.Update(folder, cancellationToken: ct);

    public async Task DeleteFolder(string id, CancellationToken ct = default)
    {
        await store.Query<FolderGrant>().Where(x => x.FolderId == id).ExecuteDelete(ct);
        await store.Query<PathAnnotation>().Where(x => x.FolderId == id).ExecuteDelete(ct);
        await store.Query<ShareLink>().Where(x => x.FolderId == id).ExecuteDelete(ct);
        await store.Remove<ShareFolder>(id, cancellationToken: ct);
    }

    public Task<IReadOnlyList<FolderGrant>> GetGrants(CancellationToken ct = default)
        => store.Query<FolderGrant>().ToList(ct);

    public Task<IReadOnlyList<FolderGrant>> GetGrantsForFolder(string folderId, CancellationToken ct = default)
        => store.Query<FolderGrant>().Where(x => x.FolderId == folderId).ToList(ct);

    public async Task<FolderGrant> SetGrant(FolderGrant grant, CancellationToken ct = default)
    {
        // one grant per (folder, role) - re-granting replaces rather than stacking, otherwise the
        // effective-permission union would silently keep an old, more generous row alive
        var existing = await store
            .Query<FolderGrant>()
            .Where(x => x.FolderId == grant.FolderId && x.RoleId == grant.RoleId)
            .ToList(ct);

        foreach (var old in existing.Where(x => x.Id != grant.Id))
            await store.Remove<FolderGrant>(old.Id, cancellationToken: ct);

        if (String.IsNullOrEmpty(grant.Id))
        {
            grant.Id = Ids.New();
            await store.Insert(grant, cancellationToken: ct);
        }
        else
        {
            await store.Upsert(grant, cancellationToken: ct);
        }

        return grant;
    }

    public async Task DeleteGrant(string id, CancellationToken ct = default)
        => await store.Remove<FolderGrant>(id, cancellationToken: ct);

    public async Task<IReadOnlyDictionary<string, PathAnnotation>> GetAnnotations(string folderId, CancellationToken ct = default)
    {
        var all = await store.Query<PathAnnotation>().Where(x => x.FolderId == folderId).ToList(ct);

        // Last one wins on a duplicate path rather than throwing: an annotation is decoration, and
        // a listing must never fail because two rows disagree about a folder's nickname.
        var map = new Dictionary<string, PathAnnotation>(StringComparer.OrdinalIgnoreCase);
        foreach (var annotation in all)
            map[Normalize(annotation.Path)] = annotation;

        return map;
    }

    public async Task SetAnnotation(string folderId, string path, string? displayName, string? description, CancellationToken ct = default)
    {
        var normalized = Normalize(path);
        var existing = await store
            .Query<PathAnnotation>()
            .Where(x => x.FolderId == folderId && x.Path == normalized)
            .ToList(ct);

        var name = Blank(displayName);
        var text = Blank(description);

        if (name is null && text is null)
        {
            foreach (var annotation in existing)
                await store.Remove<PathAnnotation>(annotation.Id, cancellationToken: ct);

            return;
        }

        if (existing.FirstOrDefault() is { } current)
        {
            current.DisplayName = name;
            current.Description = text;
            await store.Update(current, cancellationToken: ct);

            // a duplicate row for the same path would shadow this one unpredictably
            foreach (var extra in existing.Skip(1))
                await store.Remove<PathAnnotation>(extra.Id, cancellationToken: ct);
        }
        else
        {
            await store.Insert(
                new PathAnnotation
                {
                    Id = Ids.New(),
                    FolderId = folderId,
                    Path = normalized,
                    DisplayName = name,
                    Description = text
                },
                cancellationToken: ct
            );
        }
    }

    static string Normalize(string? path)
        => String.IsNullOrWhiteSpace(path) ? "" : path.Replace('\\', '/').Trim('/');

    static string? Blank(string? value) => String.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
