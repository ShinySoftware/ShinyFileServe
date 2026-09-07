using ShinyFileServe.Shared;

namespace ShinyFileServe.Web.Services;

/// <summary>
/// The docked panels are built independently by the dock host, so they cannot pass parameters to
/// each other. This is the seam between them: the tree writes the selection, the file pane and the
/// information bar read it, and everyone re-renders off the same events.
/// </summary>
public class WorkspaceState(FileServeApi api)
{
    CurrentUser? user;
    FolderInfo? folder;
    string path = "";

    /// <summary>Every share the signed-in user can see.</summary>
    public IReadOnlyList<FolderInfo> Folders { get; private set; } = [];

    public CurrentUser? User => this.user;
    public FolderInfo? Folder => this.folder;

    /// <summary>The directory inside <see cref="Folder"/> currently open, "" for the share root.</summary>
    public string Path => this.path;

    public IReadOnlyList<FileEntryInfo> Entries { get; private set; } = [];
    public bool IsLoading { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Raised when the folder, path, or entries change. Panels subscribe on init.</summary>
    public event Action? Changed;

    /// <summary>Raised when the tree needs to re-read a branch after a create or delete.</summary>
    public event Func<string, Task>? DirectoryInvalidated;

    void Notify() => this.Changed?.Invoke();

    public void SetUser(CurrentUser? value)
    {
        this.user = value;
        this.Notify();
    }

    public async Task LoadFolders(CancellationToken ct = default)
    {
        this.Folders = await api.GetFolders(ct);

        // Keep whatever the user was looking at if it is still there; otherwise open the first share
        // so the workspace is never a blank rectangle on arrival.
        if (this.folder is not null && this.Folders.FirstOrDefault(f => f.Id == this.folder.Id) is { } same)
            this.folder = same;
        else if (this.Folders.Count > 0)
            await this.OpenFolder(this.Folders[0], "", ct);

        this.Notify();
    }

    public async Task OpenFolder(FolderInfo target, string directory = "", CancellationToken ct = default)
    {
        this.folder = target;
        this.path = Normalize(directory);
        await this.Reload(ct);
    }

    public Task Navigate(string directory, CancellationToken ct = default)
    {
        this.path = Normalize(directory);
        return this.Reload(ct);
    }

    /// <summary>Re-reads the current directory and the folder's quota meters together.</summary>
    public async Task Reload(CancellationToken ct = default)
    {
        if (this.folder is null)
            return;

        this.IsLoading = true;
        this.Error = null;
        this.Notify();

        try
        {
            var listing = await api.List(this.folder.Id, this.path, ct);
            this.Entries = listing.Entries;

            // quotas move on every transfer, so the meters are refreshed with the listing rather
            // than left to drift until the next full page load
            if (await api.GetFolder(this.folder.Id, ct) is { } refreshed)
            {
                this.folder = refreshed;
                this.Folders = this.Folders.Select(f => f.Id == refreshed.Id ? refreshed : f).ToList();
            }
        }
        catch (ApiException ex)
        {
            this.Entries = [];
            this.Error = ex.Message;
        }
        finally
        {
            this.IsLoading = false;
            this.Notify();
        }
    }

    public async Task InvalidateDirectory(string directory)
    {
        if (this.DirectoryInvalidated is { } handler)
            await handler.Invoke(Normalize(directory));
    }

    /// <summary>The current path broken into clickable segments, root first.</summary>
    public IReadOnlyList<(string Label, string Path)> Crumbs()
    {
        var crumbs = new List<(string, string)> { (this.folder?.Name ?? "", "") };
        if (String.IsNullOrEmpty(this.path))
            return crumbs;

        var running = "";
        foreach (var segment in this.path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            running = running.Length == 0 ? segment : running + "/" + segment;
            crumbs.Add((segment, running));
        }
        return crumbs;
    }

    internal static string Normalize(string? value)
        => String.IsNullOrWhiteSpace(value) ? "" : value.Replace('\\', '/').Trim('/');
}
