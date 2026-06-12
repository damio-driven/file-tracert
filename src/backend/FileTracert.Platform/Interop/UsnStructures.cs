using System.Runtime.InteropServices;

namespace FileTracert.Platform.Interop;

/// <summary>Output of <c>FSCTL_QUERY_USN_JOURNAL</c> (V0 layout).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct UsnJournalDataV0
{
    public ulong UsnJournalId;
    public long FirstUsn;
    public long NextUsn;
    public long LowestValidUsn;
    public long MaxUsn;
    public ulong MaximumSize;
    public ulong AllocationDelta;
}

/// <summary>Input of <c>FSCTL_CREATE_USN_JOURNAL</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CreateUsnJournalData
{
    public ulong MaximumSize;
    public ulong AllocationDelta;
}

/// <summary>Input of <c>FSCTL_ENUM_USN_DATA</c> (V0 layout).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MftEnumDataV0
{
    public ulong StartFileReferenceNumber;
    public long LowUsn;
    public long HighUsn;
}

/// <summary>Input of <c>FSCTL_READ_USN_JOURNAL</c> (V0 layout).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ReadUsnJournalDataV0
{
    public long StartUsn;
    public uint ReasonMask;
    public uint ReturnOnlyOnClose;
    public ulong Timeout;
    public ulong BytesToWaitFor;
    public ulong UsnJournalId;
}
