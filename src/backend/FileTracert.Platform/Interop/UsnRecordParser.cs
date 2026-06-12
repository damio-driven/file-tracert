using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace FileTracert.Platform.Interop;

/// <summary>One decoded <c>USN_RECORD_V2</c>.</summary>
internal readonly record struct ParsedUsnRecord(
    uint RecordLength,
    ulong FileReferenceNumber,
    ulong ParentFileReferenceNumber,
    long Usn,
    uint Reason,
    FileAttributes Attributes,
    string Name);

/// <summary>
/// Decodes variable-length <c>USN_RECORD_V2</c> entries straight from the bytes
/// returned by <c>FSCTL_ENUM_USN_DATA</c> / <c>FSCTL_READ_USN_JOURNAL</c>.
/// Field offsets follow the documented V2 layout (fixed 60-byte header, then the
/// UTF-16 filename).
/// </summary>
internal static class UsnRecordParser
{
    private const int OffsetRecordLength = 0;
    private const int OffsetMajorVersion = 4;
    private const int OffsetFileReferenceNumber = 8;
    private const int OffsetParentFileReferenceNumber = 16;
    private const int OffsetUsn = 24;
    private const int OffsetReason = 40;
    private const int OffsetFileAttributes = 52;
    private const int OffsetFileNameLength = 56;
    private const int OffsetFileNameOffset = 58;

    /// <summary>Smallest record we can read the header from.</summary>
    internal const int HeaderLength = 60;

    /// <summary>USN_RECORD version this parser understands.</summary>
    internal const ushort SupportedMajorVersion = 2;

    /// <summary>Reads the <c>RecordLength</c> field without decoding the rest.</summary>
    public static uint ReadRecordLength(ReadOnlySpan<byte> record) =>
        BinaryPrimitives.ReadUInt32LittleEndian(record[OffsetRecordLength..]);

    /// <summary>Reads the <c>MajorVersion</c> field.</summary>
    public static ushort ReadMajorVersion(ReadOnlySpan<byte> record) =>
        BinaryPrimitives.ReadUInt16LittleEndian(record[OffsetMajorVersion..]);

    /// <summary>Decodes a single V2 record. Caller must pass exactly one record's bytes.</summary>
    public static ParsedUsnRecord Parse(ReadOnlySpan<byte> record)
    {
        var recordLength = BinaryPrimitives.ReadUInt32LittleEndian(record[OffsetRecordLength..]);
        var frn = BinaryPrimitives.ReadUInt64LittleEndian(record[OffsetFileReferenceNumber..]);
        var parentFrn = BinaryPrimitives.ReadUInt64LittleEndian(record[OffsetParentFileReferenceNumber..]);
        var usn = BinaryPrimitives.ReadInt64LittleEndian(record[OffsetUsn..]);
        var reason = BinaryPrimitives.ReadUInt32LittleEndian(record[OffsetReason..]);
        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(record[OffsetFileAttributes..]);
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[OffsetFileNameLength..]);
        var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[OffsetFileNameOffset..]);

        var nameBytes = record.Slice(nameOffset, nameLength);
        var name = MemoryMarshal.Cast<byte, char>(nameBytes).ToString();

        return new ParsedUsnRecord(
            recordLength,
            frn,
            parentFrn,
            usn,
            reason,
            (FileAttributes)attributes,
            name);
    }
}
