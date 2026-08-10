using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FileTracert.Data.Conversions;

/// <summary>
/// Stamps <see cref="DateTimeKind.Utc"/> on every <see cref="DateTime"/> read back from
/// the store. SQLite has no datetime type: values come back as TEXT with
/// <see cref="DateTimeKind.Unspecified"/>, which serialises to JSON without the trailing
/// 'Z' — so every clock in the UI reads a UTC instant as local time (review finding #12).
/// </summary>
/// <remarks>
/// The write side is deliberately <em>conditional</em>: only a <see cref="DateTimeKind.Local"/>
/// value is converted. Everything in this codebase is UTC by convention (§6) and plenty of
/// values arrive as <see cref="DateTimeKind.Unspecified"/> (file system APIs, values already
/// on disk); an unconditional <c>ToUniversalTime()</c> would reinterpret those as local time
/// and shift them by the machine offset on every save.
/// </remarks>
public sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter()
        : base(
            v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v,
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
    {
    }
}
