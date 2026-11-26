using ShinyFileServe.Shared;

namespace ShinyFileServe.Core.Services;

public class FileAccessException(string message) : Exception(message);

/// <summary>What <see cref="IFileService.Save"/> does when the name it was given is taken.</summary>
public enum SaveConflict
{
    /// <summary>Refuse the upload.</summary>
    Fail,

    /// <summary>Replace what is there.</summary>
    Overwrite,

    /// <summary>Keep both: the upload lands as <c>name (2).ext</c>, or the first number that is free.</summary>
    Rename
}

/// <summary>Where an upload landed, which differs from where it was aimed when it had to be renamed.</summary>
public record SavedFile(string VirtualPath, long Bytes);

public interface IFileService
{
    /// <summary>
    /// Turns a browser-supplied virtual path into a real one under the folder root, or throws.
    /// Every operation goes through here - it is the only thing standing between a user and the disk.
    /// </summary>
    string ResolvePath(FolderAccess access, string? virtualPath);

    IReadOnlyList<FileEntryInfo> List(FolderAccess access, string? virtualPath);
    FileInfo GetFile(FolderAccess access, string virtualPath);

    /// <summary>
    /// Writes <paramref name="content"/> to a temporary file beside the target and moves it into
    /// place once it is whole. <paramref name="inspect"/> is handed the temporary path after the
    /// last byte is written and before the move: throw from it and nothing is ever published - which
    /// is how a virus scan gets its look at a finished file without the file existing at a path
    /// anyone could list or download first. The name is settled at the move, not before the copy,
    /// so a file that appears there in the meantime is still treated as <paramref name="conflict"/> says.
    /// </summary>
    Task<SavedFile> Save(
        FolderAccess access,
        string virtualPath,
        Stream content,
        SaveConflict conflict,
        Func<string, CancellationToken, Task>? inspect = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// <see cref="Save"/> for content that is already a whole file on this machine: it is moved
    /// rather than copied, which on the same volume is a rename however large the file. The source
    /// is consumed whatever the outcome - published, refused, or failed - so it must be something
    /// the caller was about to throw away anyway, such as a finished resumable upload.
    /// </summary>
    Task<SavedFile> SaveFile(
        FolderAccess access,
        string virtualPath,
        string sourcePath,
        SaveConflict conflict,
        Func<string, CancellationToken, Task>? inspect = null,
        CancellationToken ct = default
    );

    void Delete(FolderAccess access, string virtualPath);
    FileEntryInfo CreateDirectory(FolderAccess access, string? parentVirtualPath, string name);
}

public class FileService : IFileService
{
    public string ResolvePath(FolderAccess access, string? virtualPath)
    {
        var root = Path.GetFullPath(access.Folder.PhysicalPath);
        if (String.IsNullOrWhiteSpace(virtualPath) || virtualPath is "/" or "\\")
            return root;

        var trimmed = virtualPath.Replace('\\', '/').Trim('/');

        // reject the segments outright rather than relying on the canonicalisation below to catch them,
        // so a rejected path is reported as a bad request instead of a confusing "not found"
        foreach (var segment in trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
                throw new FileAccessException("Path segments '.' and '..' are not allowed");

            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new FileAccessException($"Invalid path segment '{segment}'");
        }

        var combined = Path.GetFullPath(Path.Combine(root, trimmed));

        if (!IsUnder(combined, root))
            throw new FileAccessException("Path resolves outside the share folder");

        // The lexical check above cannot see links: share/photos -> /etc passes it, and so does every
        // path beneath it. So the real location of the deepest part that exists is checked against
        // the real location of the share. A link that stays inside the share is fine; one that leads
        // out is refused, for reading and for writing alike - an upload into a path through it would
        // otherwise create the file wherever the link points.
        var realRoot = RealPath(root);
        if (!IsUnder(RealPath(DeepestExisting(combined, root)), realRoot))
            throw new FileAccessException("Path resolves outside the share folder");

        return combined;
    }

    static bool IsUnder(string path, string root)
    {
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path == root || path.StartsWith(rootWithSep, StringComparison.Ordinal);
    }

    /// <summary>The path itself if it exists, otherwise its nearest ancestor that does, stopping at the share.</summary>
    static string DeepestExisting(string path, string root)
    {
        var current = path;
        while (current.Length > root.Length && !Path.Exists(current))
            current = Path.GetDirectoryName(current) ?? root;

        return current;
    }

    /// <summary>
    /// Where a path really is once every link along it is followed - a realpath, which .NET does not
    /// offer. Walked one component at a time from the top, because a link can sit at any level and
    /// can point at a relative target or at another link. Bounded so a cycle ends in a refusal
    /// rather than a hang.
    /// </summary>
    static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var resolved = Path.GetPathRoot(full) ?? "";
        var remaining = new Queue<string>(full[resolved.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries));
        var hops = 0;

        while (remaining.Count > 0)
        {
            var next = Path.Combine(resolved, remaining.Dequeue());
            var info = new FileInfo(next);
            var target = info.Exists || Directory.Exists(next) ? info.LinkTarget : null;

            if (target is null)
            {
                resolved = next;
            }
            else
            {
                if (++hops > 40)
                    throw new FileAccessException("Too many levels of links");

                // The target replaces this component and is itself walked, so a link to a link, or a
                // relative target with links of its own, ends up fully resolved.
                var absolute = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(resolved, target));
                var rest = remaining.ToArray();
                resolved = Path.GetPathRoot(absolute) ?? "";
                remaining = new Queue<string>(
                    absolute[resolved.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Concat(rest)
                );
            }
        }

        return resolved;
    }

    public IReadOnlyList<FileEntryInfo> List(FolderAccess access, string? virtualPath)
    {
        var full = this.ResolvePath(access, virtualPath);
        if (!Directory.Exists(full))
            throw new FileAccessException("Directory not found");

        var prefix = Normalize(virtualPath);
        var results = new List<FileEntryInfo>();

        foreach (var dir in Directory.EnumerateDirectories(full))
        {
            var info = new DirectoryInfo(dir);
            if (info.Attributes.HasFlag(FileAttributes.Hidden))
                continue;

            results.Add(new FileEntryInfo(info.Name, Join(prefix, info.Name), true, 0, info.LastWriteTimeUtc));
        }

        foreach (var file in Directory.EnumerateFiles(full))
        {
            var info = new FileInfo(file);
            if (info.Attributes.HasFlag(FileAttributes.Hidden))
                continue;

            results.Add(new FileEntryInfo(info.Name, Join(prefix, info.Name), false, info.Length, info.LastWriteTimeUtc));
        }

        return results
            .OrderByDescending(x => x.IsDirectory)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public FileInfo GetFile(FolderAccess access, string virtualPath)
    {
        var full = this.ResolvePath(access, virtualPath);
        var info = new FileInfo(full);
        if (!info.Exists)
            throw new FileAccessException("File not found");

        return info;
    }

    public Task<SavedFile> Save(
        FolderAccess access,
        string virtualPath,
        Stream content,
        SaveConflict conflict,
        Func<string, CancellationToken, Task>? inspect = null,
        CancellationToken ct = default)
        => this.SaveCore(access, virtualPath, conflict, inspect, ct, async (temp, token) =>
        {
            await using var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await content.CopyToAsync(fs, token);
            return fs.Length;
        });

    public Task<SavedFile> SaveFile(
        FolderAccess access,
        string virtualPath,
        string sourcePath,
        SaveConflict conflict,
        Func<string, CancellationToken, Task>? inspect = null,
        CancellationToken ct = default)
        => this.SaveCore(access, virtualPath, conflict, inspect, ct, async (temp, _) =>
        {
            // A rename when the source is on the same volume as the share, which costs nothing
            // whatever the size. Across volumes File.Move copies and then deletes, which is the
            // copy this replaces, so it is never worse. Off the request thread either way, since
            // the cross-volume case blocks for as long as the copy takes and cannot be cancelled.
            await Task.Run(() => File.Move(sourcePath, temp));
            return new FileInfo(temp).Length;
        });

    /// <summary>
    /// What both saves share: the checks before any bytes move, a temporary file beside the target
    /// that <paramref name="fill"/> puts the content into, the inspection, and the move into place.
    /// </summary>
    async Task<SavedFile> SaveCore(
        FolderAccess access,
        string virtualPath,
        SaveConflict conflict,
        Func<string, CancellationToken, Task>? inspect,
        CancellationToken ct,
        Func<string, CancellationToken, Task<long>> fill)
    {
        var full = this.ResolvePath(access, virtualPath);

        // Checked up front as well as at the move, so a refusal costs nothing rather than a whole copy.
        if (conflict != SaveConflict.Rename && Directory.Exists(full))
            throw new FileAccessException("A directory already exists at that path");

        if (conflict == SaveConflict.Fail && File.Exists(full))
            throw new FileAccessException("File already exists");

        var parent = Path.GetDirectoryName(full);
        if (parent is not null && !Directory.Exists(parent))
            throw new FileAccessException("Target directory does not exist");

        // write to a sibling temp file first so an aborted upload never leaves a truncated file behind
        var temp = full + ".uploading-" + Guid.NewGuid().ToString("N")[..8];
        long written;
        string published;
        try
        {
            written = await fill(temp, ct);

            // The file is complete and closed, and still nowhere a user can reach. Anything that
            // wants to refuse it says so now, by throwing; the catch below takes the bytes away.
            if (inspect is not null)
                await inspect(temp, ct);

            published = Publish(temp, full, Normalize(virtualPath), conflict);
        }
        catch
        {
            if (File.Exists(temp))
                File.Delete(temp);
            throw;
        }
        return new SavedFile(published, written);
    }

    /// <summary>Moves the finished temp file to its name and returns the virtual path it ended up at.</summary>
    static string Publish(string temp, string full, string virtualPath, SaveConflict conflict)
    {
        if (conflict == SaveConflict.Overwrite)
        {
            File.Move(temp, full, overwrite: true);
            return virtualPath;
        }

        if (conflict == SaveConflict.Fail)
        {
            try
            {
                File.Move(temp, full, overwrite: false);
            }
            catch (IOException) when (File.Exists(full) || Directory.Exists(full))
            {
                throw new FileAccessException("File already exists");
            }
            return virtualPath;
        }

        var free = Candidates(full, virtualPath).Where(c => !File.Exists(c.Full) && !Directory.Exists(c.Full));
        foreach (var candidate in free)
        {
            try
            {
                File.Move(temp, candidate.Full, overwrite: false);
                return candidate.Virtual;
            }
            catch (IOException) when (File.Exists(candidate.Full) || Directory.Exists(candidate.Full))
            {
                // Taken between the check and the move; the next number will do.
            }
        }
        throw new FileAccessException("There is no free name left for this file");
    }

    /// <summary>The name itself, then <c>name (2).ext</c>, <c>name (3).ext</c> and so on.</summary>
    static IEnumerable<(string Full, string Virtual)> Candidates(string full, string virtualPath)
    {
        yield return (full, virtualPath);

        var directory = Path.GetDirectoryName(full)!;
        var stem = Path.GetFileNameWithoutExtension(full);
        var extension = Path.GetExtension(full);
        var slash = virtualPath.LastIndexOf('/');
        var parent = slash < 0 ? "" : virtualPath[..(slash + 1)];

        for (var n = 2; n <= 9999; n++)
        {
            var name = $"{stem} ({n}){extension}";
            yield return (Path.Combine(directory, name), parent + name);
        }
    }

    public void Delete(FolderAccess access, string virtualPath)
    {
        var full = this.ResolvePath(access, virtualPath);
        if (full == Path.GetFullPath(access.Folder.PhysicalPath))
            throw new FileAccessException("The share root cannot be deleted");

        if (File.Exists(full))
            File.Delete(full);
        else if (Directory.Exists(full))
            throw new FileAccessException("Only files can be deleted");
        else
            throw new FileAccessException("File not found");
    }

    public FileEntryInfo CreateDirectory(FolderAccess access, string? parentVirtualPath, string name)
    {
        if (String.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
            throw new FileAccessException("Invalid directory name");

        var prefix = Normalize(parentVirtualPath);
        var virtualPath = Join(prefix, name.Trim());
        var full = this.ResolvePath(access, virtualPath);

        if (Directory.Exists(full) || File.Exists(full))
            throw new FileAccessException("A file or directory with that name already exists");

        var info = Directory.CreateDirectory(full);
        return new FileEntryInfo(info.Name, virtualPath, true, 0, info.LastWriteTimeUtc);
    }

    static string Normalize(string? virtualPath)
        => String.IsNullOrWhiteSpace(virtualPath) ? "" : virtualPath.Replace('\\', '/').Trim('/');

    static string Join(string prefix, string name)
        => String.IsNullOrEmpty(prefix) ? name : prefix + "/" + name;
}
