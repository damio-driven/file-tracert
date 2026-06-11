using System.Globalization;

namespace FileTracert.Platform.Internal;

/// <summary>
/// Pure, P/Invoke-free helpers for parsing/normalizing the strings that Win32
/// volume APIs hand back. Kept separate so they can be unit-tested
/// deterministically on any platform.
/// </summary>
internal static class VolumePathParsing
{
    /// <summary>
    /// True when <paramref name="value"/> looks like a volume GUID path:
    /// <c>\\?\Volume{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}\</c>.
    /// </summary>
    public static bool IsVolumeGuidPath(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        const string prefix = @"\\?\Volume{";
        const string suffix = @"}\";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) ||
            !value.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var guidPart = value.Substring(prefix.Length, value.Length - prefix.Length - suffix.Length);
        return Guid.TryParse(guidPart, out _);
    }

    /// <summary>
    /// Splits a double-null-terminated list of strings (as returned by
    /// <c>GetVolumePathNamesForVolumeName</c>) into the non-empty entries.
    /// </summary>
    public static IReadOnlyList<string> SplitDoubleNullTerminated(ReadOnlySpan<char> buffer)
    {
        var result = new List<string>();
        var start = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\0')
            {
                continue;
            }

            if (i > start)
            {
                result.Add(buffer.Slice(start, i - start).ToString());
            }

            start = i + 1;
        }

        return result;
    }

    /// <summary>
    /// Formats a 32-bit volume serial as the conventional <c>XXXX-XXXX</c> form.
    /// </summary>
    public static string FormatSerial(uint serial)
    {
        var high = (serial >> 16) & 0xFFFF;
        var low = serial & 0xFFFF;
        return string.Create(
            9,
            (high, low),
            static (span, state) =>
            {
                state.high.TryFormat(span[..4], out _, "X4", CultureInfo.InvariantCulture);
                span[4] = '-';
                state.low.TryFormat(span[5..], out _, "X4", CultureInfo.InvariantCulture);
            });
    }

    /// <summary>
    /// Extracts the drive-letter key (e.g. <c>C:</c>) from a mount point such as
    /// <c>C:\</c>, for looking up the physical-disk map. Returns null when the
    /// mount point is not a drive-letter path (e.g. a folder mount point).
    /// </summary>
    public static string? DriveLetterKey(string? mountPoint)
    {
        if (mountPoint is null || mountPoint.Length < 2 || mountPoint[1] != ':')
        {
            return null;
        }

        var letter = char.ToUpperInvariant(mountPoint[0]);
        return letter is >= 'A' and <= 'Z' ? $"{letter}:" : null;
    }
}
