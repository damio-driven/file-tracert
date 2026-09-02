using System.Collections.Concurrent;
using FileTracert.Contracts.Platform;
using FluentAssertions;

namespace FileTracert.Tests.Business;

/// <summary>
/// A probe on the FIXTURE, not on the product — and it earns its place because the fixture is
/// handed to a LIVE worker by <c>UsnSyncWorkerTests</c>, which makes a defect in it indistinguishable
/// from a defect in the applier. The failure it guards is precisely that: a delta read together with
/// a journal tail it does not belong to, checkpointed, and showing up as <c>LastUsn</c> one
/// increment behind what the test wrote — a red that names the product and blames the wrong file.
///
/// <para><b>It can only ever be red on an observed tear.</b> The loops are bounded by ITERATIONS,
/// not by a clock, and nothing here asserts that anything happened within a deadline: a slow machine
/// runs the same iterations more slowly and stays green. That is deliberate — the one thing worse
/// than a race in a fixture is a temporal test bolted on top of it.</para>
///
/// <para>The two scripts share nothing: each carries a record whose USN matches its own tail, so a
/// reader holding a record from one and the tail of the other has, provably, been handed an answer
/// that was never written.</para>
/// </summary>
public sealed class ScriptedUsnReaderTests
{
    private static UsnChangeRecord Record(long usn) =>
        new(new UsnEntry(200, 100, "a.jpg", "a.jpg", IsDirectory: false,
                SizeBytes: null, FileAttributes.Normal, Usn: usn),
            UsnReason.FileCreate | UsnReason.Close,
            IsRename: false,
            OldName: null);

    [Fact]
    public async Task A_scripted_delta_and_its_tail_are_never_read_apart()
    {
        const int Rounds = 50_000;
        const long LowUsn = 600, LowTail = 900, HighUsn = 1_600, HighTail = 1_900;

        var reader = new ScriptedUsnReader();
        reader.Script([Record(LowUsn)], nextUsn: LowTail);

        var torn = new ConcurrentBag<(long Usn, long Tail)>();

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < Rounds; i++)
            {
                reader.Script(
                    [Record(i % 2 == 0 ? HighUsn : LowUsn)],
                    nextUsn: i % 2 == 0 ? HighTail : LowTail);
            }
        });

        var consumer = Task.Run(() =>
        {
            for (var i = 0; i < Rounds; i++)
            {
                var answer = reader.ReadChanges("volume", sinceUsn: 0, journalId: 7, CancellationToken.None);
                var usn = answer.Changes.Single().Entry.Usn;

                var consistent = (usn == LowUsn && answer.NextUsn == LowTail)
                    || (usn == HighUsn && answer.NextUsn == HighTail);
                if (!consistent)
                {
                    torn.Add((usn, answer.NextUsn));
                }
            }
        });

        await Task.WhenAll(writer, consumer);

        torn.Should().BeEmpty(
            "a caller must never compose a checkpoint out of one script's records and another's tail");
    }
}
