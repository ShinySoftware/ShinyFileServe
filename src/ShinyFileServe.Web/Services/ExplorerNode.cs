using ShinyFileServe.Shared;

namespace ShinyFileServe.Web.Services;

/// <summary>
/// One row in the folder tree. A share and a directory inside it are the same shape - the tree does
/// not need to care which it is holding, only the icon and the "can I go up from here" rule differ.
/// </summary>
public class ExplorerNode
{
    public required FolderInfo Folder { get; init; }

    /// <summary>"" for the share root, otherwise the path inside the share.</summary>
    public required string Path { get; init; }

    public required string Name { get; init; }

    public bool IsShare => this.Path.Length == 0;

    /// <summary>Stable across reloads, so expansion and selection survive a refresh.</summary>
    public string Key => $"{this.Folder.Id}:{this.Path}";

    public override bool Equals(object? obj) => obj is ExplorerNode other && other.Key == this.Key;
    public override int GetHashCode() => this.Key.GetHashCode();
}
