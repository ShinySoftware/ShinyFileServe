using ShinyFileServe.Core.Services;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Server.Transfers;

/// <summary>The decisions every upload route makes the same way, whichever protocol brought the bytes.</summary>
static class UploadRules
{
    /// <summary>
    /// What a name clash does. Replacing a file destroys the old one as surely as deleting it, so
    /// overwriting takes the delete permission as well as upload; without it the upload is kept
    /// under a new name rather than refused. A caller that did not ask to overwrite is refused.
    /// </summary>
    public static SaveConflict Conflict(FolderAccess folder, bool overwrite)
    {
        if (!overwrite)
            return SaveConflict.Fail;

        return folder.Permissions.Has(FolderPermissions.DeleteFile)
            ? SaveConflict.Overwrite
            : SaveConflict.Rename;
    }

    /// <summary>The activity log's note for an upload that had to be renamed, or null when it landed where it was aimed.</summary>
    public static string? RenamedDetail(string aimedAt, SavedFile saved)
        => saved.VirtualPath == aimedAt
            ? null
            : $"Saved as {Path.GetFileName(saved.VirtualPath)} - {Path.GetFileName(aimedAt)} already exists and this account may not replace it";
}
