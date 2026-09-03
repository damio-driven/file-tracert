namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>The scenarios the harness knows about, and the config-driven selection over them.</summary>
public static class ScenarioCatalog
{
    /// <summary>Every scenario, in the order they are worth reading a report in.</summary>
    public static IReadOnlyList<Scenario> All() =>
    [
        new MoveFileIntraVolumeScenario(),
        new MoveFileCrossVolumeScenario(),
        new MoveFolderExcludedFilesScenario(),
        new MoveFolderNothingToCopyScenario(),
        new MoveFolderRejectedAtEnqueueScenario(),
        new RenameFolderScenario(),
        new CreateFolderScenario(),
        new CancelMidCopyScenario(),
        new CancelBeforeDeleteScenario(),
        new CrashResumeScenario(),
        new CrashResumeVerifyingScenario(),
        new CrashResumeDeletingSourceScenario(),
        new CrashResumeSimpleOpScenario(),
        new IntraVolumeCollisionScenario(),
        new CopyIntraVolumeScenario(),
        new CopyCrossVolumeScenario(),
        new CopyCancelledMidFlightScenario(),
        new SearchDateFilterScenario(),
        new ProjectionOverlayScenario(),
        new JobDependenciesScenario(),
        new RescanPreservesOverlayScenario(),
        new ExclusionVsAbsenceScenario(),
        new UsnIncrementalSyncScenario(),
        new UsnHiddenSubtreeScenario(),
        new IndexUpdateFailOnceScenario(),
        new PhantomReservationScenario(),
        new InsufficientSpaceScenario(),
        new ResumeSpaceRecheckScenario(),
        new LiveSpaceRecheckScenario(),
        new SpaceMarginScenario(),
        new FifoAutoRecoveryScenario(),
        new OfflineSimulatedScenario(),
        new OfflineEnqueueBlockedScenario(),
        new OfflineRemountSpaceRecheckScenario(),
        new OfflineUnplugScenario(),
    ];

    /// <summary>
    /// Applies the <c>Scenarios</c> filter. Empty or <c>*</c> means everything; otherwise only the
    /// named scenarios, matched case-insensitively. Names that match nothing are returned so the
    /// operator hears about a typo instead of silently running fewer scenarios than they asked for.
    /// </summary>
    public static (IReadOnlyList<Scenario> Selected, IReadOnlyList<string> UnknownNames) Select(
        IReadOnlyList<string>? filter)
    {
        var all = All();

        if (filter is null || filter.Count == 0 || filter.Any(f => f.Trim() == "*"))
            return (all, []);

        var wanted = new HashSet<string>(filter.Select(f => f.Trim()), StringComparer.OrdinalIgnoreCase);
        var selected = all.Where(s => wanted.Contains(s.Name)).ToList();
        var unknown = wanted.Where(w => !all.Any(s => string.Equals(s.Name, w, StringComparison.OrdinalIgnoreCase)))
                            .ToList();

        return (selected, unknown);
    }
}
