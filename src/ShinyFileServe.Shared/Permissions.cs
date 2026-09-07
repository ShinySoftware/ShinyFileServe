namespace ShinyFileServe.Shared;

/// <summary>
/// The set of operations a role may be granted against a share folder.
/// These are the five operations the product exposes; anything not listed is never permitted.
/// </summary>
[Flags]
public enum FolderPermissions
{
    None = 0,

    /// <summary>Enumerate directory contents. Without this the folder is invisible to the user.</summary>
    List = 1,

    /// <summary>Read/download a file's bytes.</summary>
    DownloadFile = 2,

    /// <summary>Upload a new file, or overwrite an existing one.</summary>
    CreateFile = 4,

    /// <summary>Delete a file.</summary>
    DeleteFile = 8,

    /// <summary>Create a sub-directory.</summary>
    CreateDirectory = 16
}

public static class FolderPermissionsExtensions
{
    public static bool Has(this FolderPermissions value, FolderPermissions flag) => (value & flag) == flag;

    public static IReadOnlyList<FolderPermissions> All { get; } =
    [
        FolderPermissions.List,
        FolderPermissions.DownloadFile,
        FolderPermissions.CreateFile,
        FolderPermissions.DeleteFile,
        FolderPermissions.CreateDirectory
    ];

    public static string ToDisplay(this FolderPermissions value) => value switch
    {
        FolderPermissions.List => "List",
        FolderPermissions.DownloadFile => "Download",
        FolderPermissions.CreateFile => "Upload",
        FolderPermissions.DeleteFile => "Delete",
        FolderPermissions.CreateDirectory => "New Folder",
        _ => value.ToString()
    };
}

/// <summary>
/// The rolling window a quota is measured over. <see cref="Total"/> never resets.
/// </summary>
public enum QuotaWindow
{
    None = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3,
    Total = 4
}
