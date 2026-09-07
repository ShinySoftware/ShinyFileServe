using ShinyFileServe.Shared;

namespace ShinyFileServe.Web.Services;

/// <summary>
/// One row in the picker that walks the server machine's folders. A mutable class rather than the
/// wire record because the tree holds these instances: making a folder inside a branch that reported
/// none has to turn its chevron on where the tree can see it, and a fresh record would be a
/// different node.
/// </summary>
public class MachineFolderNode
{
    public required string Path { get; init; }
    public required string Name { get; init; }

    /// <summary>A starting point - home, a drive - rather than somewhere reached by walking down.</summary>
    public bool IsPlace { get; init; }

    public bool HasChildren { get; set; }
    public bool IsShared { get; set; }

    public static MachineFolderNode From(MachineFolder folder, bool isPlace = false) => new()
    {
        Path = folder.Path,
        Name = folder.Name,
        IsPlace = isPlace,
        HasChildren = folder.HasChildren,
        IsShared = folder.IsShared
    };

    // The path is the identity: the same folder reached twice is the same folder.
    public override bool Equals(object? obj) => obj is MachineFolderNode other && other.Path == this.Path;
    public override int GetHashCode() => this.Path.GetHashCode();
}
