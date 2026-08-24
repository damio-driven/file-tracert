using FileTracert.Contracts.Enums;

namespace FileTracert.Data.Search;

/// <summary>
/// The vocabulary of the FTS5 <c>tags</c> column: the structural facts about a row that are
/// written into the index as synthetic tokens, so that filtering on them costs an intersection of
/// doclists rather than a resolve-and-discard over every match.
///
/// <para>Written by <c>InsertProjectedSql</c> (SQL, from the row) and read by the search (C#, from
/// the query). Both spellings of each token live here, next to each other, because a writer and a
/// reader that disagree do not fail — they silently return nothing.</para>
///
/// <para><b>Why only category and volume.</b> A token is only safe for a filter whose values are a
/// closed domain that survives the tokenizer intact. Category is an enum (ASCII letters) and volume
/// is an int, so each maps to exactly one token, injectively. Extension does not: on the real
/// catalog 639 of 1 150 distinct extensions contain characters the tokenizer treats as separators
/// (<c>_ - [ ]</c> and worse), so any encoding that keeps them readable also makes two different
/// extensions collide — and a filter that silently returns the wrong rows is worse than a slow one.
/// Extension, size and date therefore stay ordinary SQL predicates; what protects them is the
/// pinned join order, which keeps their cost proportional to the match set instead of letting the
/// planner drive from <c>Files</c> and re-run the full-text query once per candidate row.</para>
///
/// <para>The <c>ft</c> prefix is not protection — column scoping is (a user MATCH never reaches
/// this column). It is there so a token is recognisable for what it is when someone dumps the
/// index by hand.</para>
/// </summary>
internal static class FileSearchTags
{
    private const string CategoryPrefix = "ftc";
    private const string VolumePrefix = "ftv";

    /// <summary>The column holding these tokens, as FTS5 names it in a column filter.</summary>
    public const string Column = "tags";

    /// <summary>
    /// The tag list of one row, as a SQL expression over the alias <c>f</c> of <c>Files</c>.
    /// <c>Category</c> is stored as its enum name (<c>HasConversion&lt;string&gt;</c>), so
    /// <c>lower()</c> is what makes the written token equal the one <see cref="Category"/> builds.
    /// </summary>
    public const string SqlExpression =
        $"'{CategoryPrefix}' || lower(f.Category) || ' {VolumePrefix}' || f.VolumeId";

    /// <summary>The token that names a category. Enum names are ASCII letters — one token, always.</summary>
    public static string Category(FileCategory category) =>
        CategoryPrefix + category.ToString().ToLowerInvariant();

    /// <summary>The token that names a volume. An int — one token, always.</summary>
    public static string Volume(int volumeId) =>
        VolumePrefix + volumeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
