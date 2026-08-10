using FileTracert.Contracts.Enums;
using FileTracert.Data.Entities;

namespace FileTracert.Business.Operations;

/// <summary>
/// The offline gate (§1, §4): an operation may only touch volumes that are actually connected.
/// A job whose source and/or target is missing is <b>parked</b>, never attempted and never failed —
/// executing it would only produce a "no mount point" error and a terminal <c>Failed</c>, killing
/// the product's founding promise (queue now with the drive unplugged, execute at the remount).
///
/// The single place that decides WHICH volume to blame, so the enqueue path, the engine's
/// pre-execution check and the revaluator can never disagree about the reason shown to the user.
/// </summary>
public static class VolumeOfflineGate
{
    /// <summary>
    /// The block reason for the involved volumes, or <see cref="JobBlockReason.None"/> when
    /// everything needed is online. A null volume means "not involved" (e.g. CreateFolder has
    /// no source). When BOTH are missing the SOURCE is reported: without it there is nothing to
    /// read, so it is the first thing the user has to plug back in.
    /// </summary>
    public static JobBlockReason Evaluate(Volume? source, Volume? target)
    {
        if (source is { IsOnline: false }) return JobBlockReason.SourceVolumeOffline;
        if (target is { IsOnline: false }) return JobBlockReason.TargetVolumeOffline;
        return JobBlockReason.None;
    }

    /// <summary>True for the two block reasons this gate owns.</summary>
    public static bool IsOfflineReason(JobBlockReason reason) =>
        reason is JobBlockReason.SourceVolumeOffline or JobBlockReason.TargetVolumeOffline;

    /// <summary>
    /// User-facing explanation of the park. Says which volume is missing and that the operation
    /// is waiting rather than lost — it lands both in <c>OperationJob.ErrorMessage</c> and in the
    /// Notifications bell.
    /// </summary>
    public static string Describe(JobBlockReason reason, Volume? source, Volume? target) => reason switch
    {
        JobBlockReason.SourceVolumeOffline =>
            $"Il volume di origine {Name(source)} non è collegato: l'operazione resta in coda e " +
            "riparte da sola quando il volume torna disponibile.",
        JobBlockReason.TargetVolumeOffline =>
            $"Il volume di destinazione {Name(target)} non è collegato: l'operazione resta in coda e " +
            "riparte da sola quando il volume torna disponibile.",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Not an offline block reason."),
    };

    /// <summary>Label if the volume has one, else its last drive letter, else its GUID path.</summary>
    private static string Name(Volume? volume) =>
        volume is null
            ? "(sconosciuto)"
            : $"'{volume.Label ?? volume.LastDriveLetter ?? volume.VolumeGuid}'";
}
