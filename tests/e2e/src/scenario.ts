import { Api, type Volume } from './api.js';
import type { Sandbox } from './sandbox.js';

/**
 * Points the catalog at the sandbox and nothing else: the volume it sits on becomes catalogable
 * (the sandbox is on the system volume, which §4 excludes by default) and the sandbox folder
 * becomes its only watched root.
 *
 * The other volumes of the machine are left exactly as the classifier found them. They stay out
 * of every scan for the reason the product gives: a volume with no active watched root is never
 * collected by `ScanWorker`, whatever its catalogable flag says.
 */
export async function watchSandbox(api: Api, sandbox: Sandbox): Promise<Volume> {
  const volume = await api.volumeForDrive(sandbox.driveRoot);
  // Bound first: from here on the fence knows the one volume this test may name, and the call
  // that decides what gets indexed — and therefore what an operation may name as a source — is
  // itself checked.
  sandbox.fence.bindVolume(volume.id);

  await api.setCatalogable(volume.id, true);
  await api.addWatchedRoot(volume.id, sandbox.volumeRelativePath, sandbox.fence);

  // Then verified against the service's own answer rather than assumed from the calls above:
  // nothing outside the sandbox is in the catalog, therefore no source id can name anything
  // outside it.
  await sandbox.fence.assertPerimeter(api);

  return volume;
}

/** As above, plus a scan run to completion — for specs that need an index, not a scan. */
export async function watchAndScanSandbox(
  api: Api,
  sandbox: Sandbox,
  expectedFiles: number,
): Promise<Volume> {
  const volume = await watchSandbox(api, sandbox);
  await api.requestRescan(volume.id);
  await api.waitForScan(volume.id, expectedFiles);
  return volume;
}
