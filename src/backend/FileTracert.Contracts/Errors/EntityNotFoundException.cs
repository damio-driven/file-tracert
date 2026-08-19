namespace FileTracert.Contracts.Errors;

/// <summary>
/// The entity named by the caller does not exist. Typed on purpose (K11): the HTTP layer used to
/// tell "gone" from "wrong" by running <c>ex.Message.Contains("not found")</c> over an
/// <see cref="InvalidOperationException"/>, so rewording a sentence — or translating it, which
/// this codebase does for every message the user reads — silently turned a 404 into a 400.
///
/// <para>Deliberately NOT <see cref="KeyNotFoundException"/>, which the BCL also throws for a
/// missing dictionary key: the queue does plenty of dictionary lookups, and a genuine bug in one
/// of them must surface as a fault, not as a polite "no such job".</para>
/// </summary>
/// <param name="entity">What was looked for ("Job", "File", "Volume") — for the log line.</param>
/// <param name="id">Its identifier.</param>
/// <param name="message">The sentence the caller reads.</param>
public sealed class EntityNotFoundException(string entity, object id, string message)
    : Exception(message)
{
    public string Entity { get; } = entity;

    public object Id { get; } = id;

    /// <summary>The usual phrasing, so the sentence is written once.</summary>
    public static EntityNotFoundException For(string entity, object id) =>
        new(entity, id, $"{entity} {id} not found.");
}
