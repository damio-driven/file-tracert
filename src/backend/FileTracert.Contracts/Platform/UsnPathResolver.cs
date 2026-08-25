namespace FileTracert.Contracts.Platform;

/// <summary>Minimal info needed to walk an FRN up to the volume root.</summary>
public readonly record struct FrnNode(string Name, ulong ParentFrn, bool IsDirectory);

/// <summary>
/// Pure, deterministic reconstruction of a path (relative to the volume root)
/// from an FRN → <see cref="FrnNode"/> map. No P/Invoke — this is the testable
/// heart of the USN reader.
/// </summary>
/// <remarks>
/// <para>Lives in the shared kernel (§3) for the same reason <c>ScanPath</c> does: two layers
/// have to spell the same rule. <c>Platform</c> uses it on the full MFT snapshot, where the map
/// holds every record on the volume and the walk always terminates at the root; <c>Business</c>
/// uses it on an incremental delta, where most parents are NOT in the delta and come from the
/// catalog instead. Nothing here touches Win32 or an entity — only BCL types and
/// <see cref="FrnUtil"/>.</para>
/// <para>The <c>knownPath</c> fallback is what makes the incremental case work: it answers "this
/// FRN already has a place in the catalog, and here it is", ending the walk early. Returning null
/// for an FRN is meaningful in itself — a parent the catalog has never heard of is a directory
/// the scan did not index, which is exactly how the incremental path inherits the subtree
/// exclusion of C16 without re-reading the disk.</para>
/// </remarks>
public sealed class UsnPathResolver
{
    /// <summary>Defensive cap against cyclic parent chains.</summary>
    public const int MaxDepth = 1024;

    private readonly IReadOnlyDictionary<ulong, FrnNode> _map;
    private readonly ulong _rootFrn;
    private readonly Func<ulong, string?>? _knownPath;

    /// <param name="map">FRNs this walk can resolve by name + parent.</param>
    /// <param name="rootFrn">The volume root's FRN; it resolves to the empty path.</param>
    /// <param name="knownPath">
    /// Optional lookup for FRNs outside <paramref name="map"/> that already have a known relative
    /// path (the catalog's directory rows). Consulted only when the map misses.
    /// </param>
    public UsnPathResolver(
        IReadOnlyDictionary<ulong, FrnNode> map,
        ulong rootFrn,
        Func<ulong, string?>? knownPath = null)
    {
        _map = map;
        _rootFrn = rootFrn;
        _knownPath = knownPath;
    }

    /// <summary>
    /// Resolves the path of <paramref name="frn"/> relative to the volume root
    /// (no leading separator). The root itself resolves to the empty string.
    /// Returns false when the chain cannot reach the root (orphan / missing
    /// parent / cycle) — the caller skips such entries.
    /// </summary>
    public bool TryResolve(ulong frn, out string relativePath)
    {
        if (frn == _rootFrn)
        {
            relativePath = string.Empty;
            return true;
        }

        // Collect names leaf-first while walking up to the volume root.
        var segments = new List<string>();
        var current = frn;

        for (var depth = 0; depth <= MaxDepth; depth++)
        {
            if (current == _rootFrn)
            {
                return Join(segments, prefix: null, out relativePath);
            }

            if (_map.TryGetValue(current, out var node))
            {
                if (node.ParentFrn == current)
                {
                    // Self-referential entry = the volume root directory itself.
                    return Join(segments, prefix: null, out relativePath);
                }

                segments.Add(node.Name);
                current = node.ParentFrn;
                continue;
            }

            // Not in the delta/snapshot: ask whether the catalog already places it.
            if (_knownPath?.Invoke(current) is { } known)
            {
                return Join(segments, known, out relativePath);
            }

            relativePath = string.Empty;
            return false; // orphan: parent not available anywhere
        }

        relativePath = string.Empty;
        return false; // depth cap hit: treat as a cycle
    }

    private static bool Join(List<string> leafFirstSegments, string? prefix, out string relativePath)
    {
        leafFirstSegments.Reverse();
        var tail = string.Join('\\', leafFirstSegments);

        relativePath = string.IsNullOrEmpty(prefix)
            ? tail
            : tail.Length == 0 ? prefix : $"{prefix}\\{tail}";
        return true;
    }
}
