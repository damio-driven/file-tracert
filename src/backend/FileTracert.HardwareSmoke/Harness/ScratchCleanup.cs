namespace FileTracert.HardwareSmoke.Harness;

/// <summary>
/// Removes what the harness created and nothing else: only the scratch area inside each configured
/// test-volume folder, never the folder itself and never anything the operator put there.
///
/// Files the scenarios sent to the Recycle Bin are deliberately left there — that is the whole
/// point of the no-hard-delete rule (§6): a run that went wrong stays undoable. Their location is
/// reported instead of being emptied.
/// </summary>
public static class ScratchCleanup
{
    public static void Run(IReadOnlyList<TestVolume> volumes, IHarnessConsole console)
    {
        foreach (var volume in volumes)
        {
            try
            {
                if (!Directory.Exists(volume.ScratchFullPath))
                    continue;

                Directory.Delete(volume.ScratchFullPath, recursive: true);
                console.Write($"cleaned up '{volume.ScratchFullPath}'.");
            }
            catch (Exception ex)
            {
                // Not silent (§9): a locked fixture is left behind and the operator is told where.
                console.Write(
                    $"could not clean up '{volume.ScratchFullPath}': {ex.GetType().Name}: {ex.Message} — " +
                    "remove it manually.");
            }
        }

        console.Write(
            "files the scenarios deleted were sent to the Recycle Bin of their volume and were NOT " +
            "emptied: restore them from there if a run went wrong.");
    }
}
