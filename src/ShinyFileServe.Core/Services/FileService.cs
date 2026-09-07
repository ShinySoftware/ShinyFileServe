using ShinyFileServe.Shared;

namespace ShinyFileServe.Core.Services;

public class FileAccessException(string message) : Exception(message);

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
    /// anyone could list or download first.
    /// </summary>
    Task<long> Save(
        FolderAccess access,
        string virtualPath,
        Stream content,
        bool overwrite,
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

        // final guard: symlinks and case-folding can still land outside the root
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootWithSep, StringComparison.Ordinal) && combined != root)
            throw new FileAccessException("Path resolves outside the share folder");

        return combined;
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

    public async Task<long> Save(
        FolderAccess access,
        string virtualPath,
        Stream content,
        bool overwrite,
        Func<string, CancellationToken, Task>? inspect = null,
        CancellationToken ct = default)
    {
        var full = this.ResolvePath(access, virtualPath);
        if (Directory.Exists(full))
            throw new FileAccessException("A directory already exists at that path");

        if (File.Exists(full) && !overwrite)
            throw new FileAccessException("File already exists");

        var parent = Path.GetDirectoryName(full);
        if (parent is not null && !Directory.Exists(parent))
            throw new FileAccessException("Target directory does not exist");

        // write to a sibling temp file first so an aborted upload never leaves a truncated file behind
        var temp = full + ".uploading-" + Guid.NewGuid().ToString("N")[..8];
        long written;
        try
        {
            await using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await content.CopyToAsync(fs, ct);
                written = fs.Length;
            }

            // The file is complete and closed, and still nowhere a user can reach. Anything that
            // wants to refuse it says so now, by throwing; the catch below takes the bytes away.
            if (inspect is not null)
                await inspect(temp, ct);

            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            if (File.Exists(temp))
                File.Delete(temp);
            throw;
        }
        return written;
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
