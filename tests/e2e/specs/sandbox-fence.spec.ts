import path from 'node:path';

import { expect, test } from '../src/fixtures.js';
import type { RecordedJob } from '../src/fence.js';
import { watchAndScanSandbox, watchSandbox } from '../src/scenario.js';

/**
 * The fence itself, tested the only way that means anything: by trying to get out.
 *
 * Every other spec in this checkpoint queues operations that the real engine carries out on the
 * volume the suite makes catalogable — the system volume of whoever runs it. What keeps that from
 * being a way to move a stranger's files is the three layers in `src/fence.ts`, and a guard nobody
 * ever fires is a guard nobody knows works. So each layer gets an attempt aimed straight at it,
 * and the test passes only when the attempt is refused, by name, with nothing created.
 *
 * The attempts point **just outside** the fence — the sandbox's own parent folder, still under
 * `.artifacts` — and never at a folder of the system. That is deliberate: the way to prove this
 * spec is not vacuous is to disable the fence and watch it go red, and a destination like
 * `Windows\Temp` would turn that demonstration into the accident the fence exists to prevent.
 * Just outside is just as outside.
 */
test.describe('Recinto della sandbox', () => {
  test('rifiuta una destinazione fuori dalla sandbox prima che il servizio la veda', async ({
    sandbox,
    api,
  }) => {
    const seeded = await sandbox.seed();
    const volume = await watchAndScanSandbox(api, sandbox, seeded.fileCount);
    const { files } = await api.walkCatalog(volume.id);
    const victim = files[0]!;

    // One level up from the watched folder: an ordinary path, and not ours. This is the mistake
    // the fence exists for — a typo in a test, not a malicious one.
    const justOutside = path.dirname(sandbox.volumeRelativePath);

    await expect(
      api.enqueue(
        {
          type: 'MoveFile',
          sourceFileId: victim.id,
          targetVolumeId: volume.id,
          targetRelativePath: justOutside,
        },
        sandbox.fence,
      ),
    ).rejects.toThrow(/SANDBOX FENCE.*resolves outside the sandbox/s);

    // Walking out with `..` is refused too, and refused as unreadable rather than resolved: the
    // product never writes a path like this, so seeing one means something upstream is wrong.
    await expect(
      api.enqueue(
        {
          type: 'CreateFolder',
          targetVolumeId: volume.id,
          targetRelativePath: `${sandbox.volumeRelativePath}\\..\\fuori`,
        },
        sandbox.fence,
      ),
    ).rejects.toThrow(/SANDBOX FENCE/);

    // The sibling trap: a prefix compare without the separator would accept this.
    await expect(
      api.enqueue(
        {
          type: 'CreateFolder',
          targetVolumeId: volume.id,
          targetRelativePath: `${sandbox.volumeRelativePath}-altrove\\x`,
        },
        sandbox.fence,
      ),
    ).rejects.toThrow(/SANDBOX FENCE/);

    // Another volume, with a path that would be perfectly legal on ours.
    await expect(
      api.enqueue(
        {
          type: 'MoveFile',
          sourceFileId: victim.id,
          targetVolumeId: volume.id + 1000,
          targetRelativePath: sandbox.volumeRelativePath,
        },
        sandbox.fence,
      ),
    ).rejects.toThrow(/SANDBOX FENCE.*is not the sandbox volume/s);

    // A rename is the other way out: a name that is really a path relocates the file.
    await expect(
      api.enqueue(
        { type: 'RenameFile', sourceFileId: victim.id, newName: '..\\fuori.jpg' },
        sandbox.fence,
      ),
    ).rejects.toThrow(/SANDBOX FENCE/);

    // None of it reached the service: refusing after the fact would be an apology, not a fence.
    expect(await api.jobs()).toHaveLength(0);
  });

  test('rifiuta un accodamento partito dal browser', async ({ page, sandbox, api }) => {
    const volume = await watchSandbox(api, sandbox);
    const justOutside = `${path.dirname(sandbox.volumeRelativePath)}\\cartella-fuori-recinto`;

    await page.goto('/dashboard');

    // The move picker only ever offers catalogued folders, so the SPA cannot ask for this on its
    // own — which is exactly why the attempt has to be made by hand. What is being tested is the
    // interception in the browser context, the layer that covers every request the *screen* sends.
    const outcome = await page.evaluate(
      async (input) => {
        try {
          const response = await fetch('/api/operations/enqueue', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'X-FileTracert-Token': input.token },
            body: JSON.stringify({
              type: 'CreateFolder',
              targetVolumeId: input.volumeId,
              targetRelativePath: input.target,
            }),
          });
          return `sent, service answered ${response.status}`;
        } catch (error) {
          return `refused: ${String(error)}`;
        }
      },
      { token: api.token, volumeId: volume.id, target: justOutside },
    );

    expect(outcome).toMatch(/^refused:/);

    // Drained here so the context teardown does not fail this test over the violation it provoked
    // on purpose — and asserted, so the refusal names what it stopped.
    const violations = sandbox.fence.takeBrowserViolations();
    expect(violations).toHaveLength(1);
    expect(violations[0]).toContain('cartella-fuori-recinto');

    expect(await api.jobs()).toHaveLength(0);
  });

  test('l’audit finale riconosce un lavoro registrato fuori dalla sandbox', async ({
    sandbox,
    api,
  }) => {
    const volume = await watchSandbox(api, sandbox);

    const outside: RecordedJob = {
      id: 7,
      type: 'MoveFile',
      isIntraVolume: true,
      sourceVolumeId: volume.id,
      targetVolumeId: volume.id,
      sourcePath: `${path.dirname(sandbox.volumeRelativePath)}\\qualcosa-di-qualcun-altro.docx`,
      targetPath: sandbox.volumeRelativePath,
    };
    expect(() => sandbox.fence.auditRecordedJobs([outside])).toThrow(
      /SANDBOX FENCE.*qualcosa-di-qualcun-altro\.docx/s,
    );

    // Cross-volume is a violation on its own: it is the only path that deletes a source file, and
    // it deletes it into the recycle bin — where this suite must never put anything.
    const crossVolume: RecordedJob = {
      id: 8,
      type: 'MoveFile',
      isIntraVolume: false,
      sourceVolumeId: volume.id,
      targetVolumeId: volume.id,
      sourcePath: `${sandbox.volumeRelativePath}\\foto\\a.jpg`,
      targetPath: sandbox.volumeRelativePath,
    };
    expect(() => sandbox.fence.auditRecordedJobs([crossVolume])).toThrow(/cross-volume/);

    // And the shape the real jobs of this suite have passes without complaint, or the audit would
    // be an alarm that always rings.
    const inside: RecordedJob = { ...crossVolume, id: 9, isIntraVolume: true };
    expect(() => sandbox.fence.auditRecordedJobs([inside])).not.toThrow();
  });
});
