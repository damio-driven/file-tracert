namespace FileTracert.Contracts.Operations;

/// <summary>
/// The reservation one job holds on the queue: the bytes it takes on the target and the bytes it
/// gives back on the source when it completes, plus the FIFO position that makes the ledger's
/// order-aware feasibility possible.
///
/// <para>It exists so "does this job hold a reservation, and which one?" is answered in exactly one
/// place (K3). Two call sites — the retry and the release of a Blocked job — each spelled the same
/// three-part guard and the same release-then-reserve pair by hand; a job whose demand answered
/// differently in the two would be reserved twice or not at all, and every later feasibility
/// verdict is computed from that number.</para>
///
/// <para>Primitives only: <c>ISpaceLedger</c> lives in the shared kernel and must keep knowing
/// nothing about the entity model (§3). The mapping from an <c>OperationJob</c> to this lives in
/// Business, next to the ledger implementation.</para>
/// </summary>
public readonly record struct JobReservation(
    int JobId,
    int SequenceOrder,
    int TargetVolumeId,
    long RequiredBytes,
    int? SourceVolumeId,
    long FreedBytes);
