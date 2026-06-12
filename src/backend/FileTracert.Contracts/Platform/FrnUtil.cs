namespace FileTracert.Contracts.Platform;

/// <summary>
/// Helpers for NTFS file reference numbers (FRN). A USN FRN packs a sequence
/// number in the high 16 bits and the MFT record index in the low 48 bits.
/// We persist the <em>full</em> 64-bit FRN (so incremental USN deltas re-match)
/// and mask to the index only when comparing against known indices (e.g. root == 5).
/// </summary>
public static class FrnUtil
{
    /// <summary>Mask isolating the MFT record index (low 48 bits) from a full FRN.</summary>
    public const ulong MftRecordIndexMask = 0x0000_FFFF_FFFF_FFFF;

    /// <summary>The NTFS root directory always lives at MFT record index 5.</summary>
    public const ulong RootMftIndex = 5;

    /// <summary>Returns the MFT record index (low 48 bits) of a full FRN.</summary>
    public static ulong MftIndex(ulong frn) => frn & MftRecordIndexMask;

    /// <summary>True when the FRN refers to the volume root directory (index 5).</summary>
    public static bool IsRoot(ulong frn) => MftIndex(frn) == RootMftIndex;
}
