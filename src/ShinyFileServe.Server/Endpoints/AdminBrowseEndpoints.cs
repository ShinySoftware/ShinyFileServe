using Shiny.Net.HttpServer;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server.Security;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Server.Endpoints;

/// <summary>
/// The server machine's own folders, so an administrator can pick the folder to publish instead of
/// typing a path they cannot see. Nothing here reads a file or reports one - only directory names,
/// which is all a picker needs.
///
/// This is administrator-only and is no wider than what an administrator already has: the share
/// editor has always accepted any path on the machine, and this only shows what is there to type.
/// </summary>
[Route("/api/admin/browse")]
[Authorize(Roles = ClaimTypesEx.AdminRole)]
public class AdminBrowseEndpoints(
    IFolderManager folders,
    ICurrentUserResolver current,
    IActivityLogger activity
)
{
    /// <summary>Two folders are the same folder if the platform's file system says so.</summary>
    static StringComparison PathComparison => OperatingSystem.IsLinux()
        ? StringComparison.Ordinal
        : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// The folders directly inside <paramref name="path"/>. With no path it answers the starting
    /// points - home, the usual places, and the drives - because a picker has to open somewhere and
    /// the file system root is a poor first screen on every platform.
    /// </summary>
    [Get]
    [Produces(200, typeof(MachineFolderListing))]
    [Produces(400)]
    [Produces(403)]
    [Produces(404)]
    public async Task<IActionResult> List(string? path, bool showHidden = false, CancellationToken ct = default)
    {
        var shared = (await folders.GetFolders(ct)).Select(f => f.PhysicalPath).ToList();

        if (String.IsNullOrWhiteSpace(path))
            return new OkObjectResult(new MachineFolderListing("", null, Places(shared)));

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

            // A root ("/" or "C:\") trims to something that is no longer a path. Keep it whole.
            if (full.Length == 0)
                full = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new BadRequestObjectResult("That is not a path this machine can resolve.");
        }

        if (!Directory.Exists(full))
            return new NotFoundObjectResult($"There is no folder at {full}.");

        List<MachineFolder> children;
        try
        {
            children = Read(full, showHidden, shared);
        }
        catch (UnauthorizedAccessException)
        {
            return new ForbidResult();
        }
        catch (IOException ex)
        {
            return new BadRequestObjectResult($"That folder could not be read. {ex.Message}");
        }

        return new OkObjectResult(new MachineFolderListing(full, ParentOf(full), children));
    }

    /// <summary>
    /// Makes a new folder to publish. The picker needs it: a folder that does not exist yet cannot
    /// be chosen from a list, and the alternative is sending the administrator to a terminal.
    /// </summary>
    [Post("/directory")]
    [Produces(200, typeof(MachineFolder))]
    [Produces(400)]
    [Produces(403)]
    public async Task<IActionResult> Create(CreateMachineFolderRequest request, HttpContext ctx, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0)
            return new BadRequestObjectResult("The folder needs a name.");

        // One segment, and not a way back up the tree. Everything else is the file system's business.
        if (name is "." or ".."
            || name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return new BadRequestObjectResult("A folder name cannot contain a path. Choose where it goes first, then name it.");

        if (String.IsNullOrWhiteSpace(request.Parent) || !Directory.Exists(request.Parent))
            return new BadRequestObjectResult("Choose the folder it goes in first.");

        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Parent));
        var full = Path.Combine(parent, name);

        if (Directory.Exists(full))
            return new BadRequestObjectResult($"There is already a folder called {name} there.");

        try
        {
            Directory.CreateDirectory(full);
        }
        catch (UnauthorizedAccessException)
        {
            return new BadRequestObjectResult($"The server is not allowed to write in {parent}.");
        }
        catch (IOException ex)
        {
            return new BadRequestObjectResult($"That folder could not be created. {ex.Message}");
        }

        await this.Log(ctx, full, ct);
        return new OkObjectResult(new MachineFolder(full, name, false, false));
    }

    /// <summary>
    /// Where a picker opens. Anywhere on the machine is still reachable - the root is one of these -
    /// but the folder somebody means is nearly always one of the first few.
    /// </summary>
    static List<MachineFolder> Places(IReadOnlyList<string> shared)
    {
        var places = new List<MachineFolder>();
        var seen = new HashSet<string>(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

        void Add(string? path, string name)
        {
            if (String.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return;

            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (full.Length == 0)
                full = Path.GetFullPath(path);

            if (seen.Add(full))
                places.Add(new MachineFolder(full, name, HasChildren(full), IsShared(full, shared)));
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Home");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Desktop");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents");
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), "Downloads");

        if (OperatingSystem.IsWindows())
        {
            foreach (var drive in Drives())
                Add(drive.RootDirectory.FullName, DriveName(drive));
        }
        else
        {
            Add("/", "This machine");

            // Mounted volumes are under the root already, but nobody thinks of an external disk as
            // a sub-folder of anything.
            if (OperatingSystem.IsMacOS() && Directory.Exists("/Volumes"))
            {
                foreach (var volume in SafeDirectories("/Volumes").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                    Add(volume, Path.GetFileName(volume));
            }
        }

        return places;
    }

    static IEnumerable<DriveInfo> Drives()
    {
        DriveInfo[] all;
        try
        {
            all = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var drive in all)
        {
            var ready = false;
            try
            {
                ready = drive.IsReady;
            }
            catch (IOException)
            {
                // A drive that throws on the way to being described is a drive nobody can share.
            }

            if (ready)
                yield return drive;
        }
    }

    static string DriveName(DriveInfo drive)
    {
        try
        {
            return String.IsNullOrWhiteSpace(drive.VolumeLabel)
                ? drive.Name
                : $"{drive.VolumeLabel} ({Path.TrimEndingDirectorySeparator(drive.Name)})";
        }
        catch (IOException)
        {
            return drive.Name;
        }
    }

    static List<MachineFolder> Read(string full, bool showHidden, IReadOnlyList<string> shared)
        => new DirectoryInfo(full)
            .EnumerateDirectories()
            .Where(d => showHidden || !IsHidden(d))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new MachineFolder(
                Path.TrimEndingDirectorySeparator(d.FullName),
                d.Name,
                HasChildren(d.FullName),
                IsShared(d.FullName, shared)
            ))
            .ToList();

    /// <summary>
    /// Whether the chevron is worth drawing. Guessed rather than reported, because being wrong costs
    /// an empty branch and being slow costs every listing on a network mount.
    /// </summary>
    static bool HasChildren(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).Any();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    static bool IsHidden(DirectoryInfo directory)
    {
        if (directory.Name.StartsWith('.'))
            return true;

        try
        {
            return directory.Attributes.HasFlag(FileAttributes.Hidden);
        }
        catch (IOException)
        {
            return false;
        }
    }

    static bool IsShared(string path, IReadOnlyList<string> shared)
    {
        var full = Path.TrimEndingDirectorySeparator(path);
        return shared.Any(s => String.Equals(Path.TrimEndingDirectorySeparator(s), full, PathComparison));
    }

    static string? ParentOf(string full)
    {
        var parent = Directory.GetParent(full)?.FullName;
        return parent is null ? null : Path.TrimEndingDirectorySeparator(parent) is { Length: > 0 } trimmed ? trimmed : parent;
    }

    async Task Log(HttpContext ctx, string path, CancellationToken ct)
    {
        if (await current.Resolve(ctx, ct) is not { } me)
            return;

        await activity.Log(
            ActivityAction.CreateDirectory, me.Id, me.Username, success: true,
            detail: $"Created the folder {path} on this machine",
            context: current.GetActivityContext(ctx), ct: ct
        );
    }
}
