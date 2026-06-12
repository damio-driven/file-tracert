namespace FileTracert.Platform.Internal;

/// <summary>Minimal info needed to walk an FRN up to the volume root.</summary>
internal readonly record struct FrnNode(string Name, ulong ParentFrn, bool IsDirectory);

/// <summary>
/// Pure, deterministic reconstruction of a path (relative to the volume root)
/// from an FRN → <see cref="FrnNode"/> map. No P/Invoke — this is the testable
/// heart of the USN reader.
/// </summary>
/// <remarks>
/// An optional fallback resolver lets the incremental path (step 4) supply
/// parents that are not in the current delta — e.g. by looking them up in the
/// already-known <c>Directories</c> tree.
/// </remarks>
internal sealed class UsnPathResolver
{
    /// <summary>Defensive cap against cyclic parent chains.</summary>
    internal const int MaxDepth = 1024;

    private readonly IReadOnlyDictionary<ulong, FrnNode> _map;
    private readonly ulong _rootFrn;
    private readonly Func<ulong, FrnNode?>? _fallback;

    public UsnPathResolver(
        IReadOnlyDictionary<ulong, FrnNode> map,
        ulong rootFrn,
        Func<ulong, FrnNode?>? fallback = null)
    {
        _map = map;
        _rootFrn = rootFrn;
        _fallback = fallback;
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
                segments.Reverse();
                relativePath = string.Join('\\', segments);
                return true;
            }

            if (!TryLookup(current, out var node))
            {
                relativePath = string.Empty;
                return false; // orphan: parent not available anywhere
            }

            if (node.ParentFrn == current)
            {
                // Self-referential entry = the volume root directory itself.
                segments.Reverse();
                relativePath = string.Join('\\', segments);
                return true;
            }

            segments.Add(node.Name);
            current = node.ParentFrn;
        }

        relativePath = string.Empty;
        return false; // depth cap hit: treat as a cycle
    }

    private bool TryLookup(ulong frn, out FrnNode node)
    {
        if (_map.TryGetValue(frn, out node))
        {
            return true;
        }

        var fromFallback = _fallback?.Invoke(frn);
        if (fromFallback.HasValue)
        {
            node = fromFallback.Value;
            return true;
        }

        node = default;
        return false;
    }
}
