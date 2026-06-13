namespace FileTracert.Contracts.Platform;

/// <summary>
/// One immediate sub-folder discovered while browsing the real filesystem for
/// setup (not the index). <see cref="RelativePath"/> is relative to the volume
/// root, normalized to backslash with no leading/trailing separator.
/// </summary>
/// <param name="HasChildren">True when the folder has at least one accessible sub-folder (drives the tree-picker expander).</param>
public sealed record FolderNode(string Name, string RelativePath, bool HasChildren);
