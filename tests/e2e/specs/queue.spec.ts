import { expect, test } from '../src/fixtures.js';
import type { Api, Job } from '../src/api.js';
import type { Sandbox } from '../src/sandbox.js';
import { watchAndScanSandbox } from '../src/scenario.js';
import {
  catalogVolume,
  jobBlockDetail,
  jobRow,
  jobState,
  openFolder,
  openPath,
  picker,
} from '../src/screens.js';

/**
 * What the Coda tells a user when an operation cannot run — and it says it about jobs that are
 * really stuck, not about states arranged for the occasion.
 *
 * The first job is stopped by a name that is already taken at the destination: the engine tries,
 * refuses to overwrite anything, and parks it `Blocked(NameCollision)`, which is recoverable and
 * offers "Riprova". The second one asks for the same file while the first still owns it, so step
 * 9c gives it `Blocked(DependencyPending)` and a pointer to what it is waiting for — a queued job
 * with an explanation, which is the thing that replaced the old 409.
 *
 * Not reachable here, and deliberately not faked: `Blocked(InsufficientSpace)` with its deficit and
 * safety margin. Space is only reserved for **cross-volume** moves, and a second volume in this
 * suite would mean writing outside the sandbox and deleting into the recycle bin. That path stays
 * covered on real drives by the hardware harness and, on the numbers, by xUnit. What *can* be shown
 * on screen is its dry run — see the preview test below.
 */
test.describe('Coda', () => {
  test("dice che un nome è già preso, e chi sta aspettando chi", async ({ page, sandbox, api }) => {
    const { blocked, dependent } = await twoJobsOnTheSameFile(api, sandbox);

    await page.goto('/queue');

    // Both are parked, and the screen counts them as such.
    await expect(page.locator('.stat-chip--block')).toContainText('2');

    // The one that ran and could not finish says what stopped it, and offers the only button that
    // can move it on. Nothing was overwritten: the file is still where it was.
    await expect(jobState(page, blocked.id)).toHaveText('Bloccato');
    await expect(jobRow(page, blocked.id).locator('.block-reason')).toHaveText('Conflitto di nome');
    await expect(jobRow(page, blocked.id).getByRole('button', { name: 'Riprova' })).toBeVisible();
    expect(await sandbox.exists('foto', 'foto-0001.jpg')).toBe(true);

    // The one that never ran says whose turn it is first, with the number linked to that row.
    await expect(jobState(page, dependent.id)).toHaveText('Bloccato');
    await expect(jobBlockDetail(page, dependent.id)).toContainText("In attesa dell'operazione");
    const link = jobBlockDetail(page, dependent.id).locator('a.dep-link');
    await expect(link).toHaveText(`#${blocked.id}`);
    await expect(link).toHaveAttribute('href', `/queue?job=${blocked.id}`);

    await link.click();
    await expect(jobRow(page, blocked.id)).toHaveClass(/job-row--focused/);

    // Take the obstacle away and press the button the screen offered. Both jobs finish: the first
    // because its destination is free, the second because the entity it was waiting for is.
    await sandbox.remove('archivio', 'foto-0001.jpg');
    await jobRow(page, blocked.id).getByRole('button', { name: 'Riprova' }).click();

    await api.waitForJobState(blocked.id, 'Completed');
    await api.waitForJobState(dependent.id, 'Completed');
    expect(await sandbox.exists('archivio', 'rinominato.jpg')).toBe(true);
    expect(await sandbox.exists('foto', 'foto-0001.jpg')).toBe(false);
  });

  test('annullare un prerequisito parcheggia chi lo aspetta, non lo cancella', async ({
    page,
    sandbox,
    api,
  }) => {
    const { blocked, dependent } = await twoJobsOnTheSameFile(api, sandbox);

    await page.goto('/queue');
    await expect(jobState(page, dependent.id)).toHaveText('Bloccato');

    await jobRow(page, blocked.id).locator('.cancel-btn').click();
    await expect(jobState(page, blocked.id)).toHaveText('Annullato');

    // §5: the dependent is parked, not cancelled with it. Restarting it is the user's decision —
    // freeing it on its own would silently redo what the user has just decided not to do.
    await expect(jobState(page, dependent.id)).toHaveText('Bloccato');
    await expect(jobBlockDetail(page, dependent.id)).toContainText('Dipendenza interrotta:');
    await expect(jobBlockDetail(page, dependent.id).locator('a.dep-link')).toHaveText(
      `#${blocked.id}`,
    );
    await expect(jobRow(page, dependent.id).getByRole('button', { name: 'Riprova' })).toBeVisible();

    // And "Riprova" is what it takes: the rename then runs where the file actually is.
    await jobRow(page, dependent.id).getByRole('button', { name: 'Riprova' }).click();
    await api.waitForJobState(dependent.id, 'Completed');
    expect(await sandbox.exists('foto', 'rinominato.jpg')).toBe(true);
  });
});

test.describe('Fattibilità', () => {
  test('la verifica spazio dichiara il margine, e non accoda niente', async ({
    page,
    sandbox,
    api,
  }) => {
    const seeded = await sandbox.seed();
    await watchAndScanSandbox(api, sandbox, seeded.fileCount);
    const letter = sandbox.driveRoot.slice(0, 2);

    await page.goto('/catalog');
    await catalogVolume(page, letter).click();
    await openPath(page, sandbox.pathSegments);
    await openFolder(page, 'foto');
    await page.getByLabel('Seleziona foto-0001.jpg').check();
    await page.getByRole('button', { name: 'Sposta selezionati…' }).click();

    // Space is only reserved across volumes, so the demand is only a number worth showing when the
    // destination is another volume. This is a **dry run** (§7: "without creating any DB record"),
    // which is the only reason a spec of this suite may point anywhere but the sandbox — and if it
    // ever went further than a preview, the fence would refuse the enqueue by volume id.
    const dialog = picker(page);
    const others = await dialog.locator('#picker-vol').evaluate(
      (element, mine) =>
        Array.from((element as HTMLSelectElement).options)
          .map((option, index) => ({ index, text: option.text }))
          .filter((o) => o.index > 0 && !o.text.includes(`(${mine})`) && !o.text.includes('offline')),
      letter,
    );
    test.skip(others.length === 0, 'la macchina ha un solo volume catalogabile online');

    await dialog.locator('#picker-vol').selectOption({ index: others[0]!.index });
    await page.getByRole('button', { name: 'Verifica spazio' }).click();

    // 200 B of file, plus the safety margin step 11b finally gave a consumer: a percentage of the
    // demand, and named as such. A bare deficit would be a number nobody can reconcile with the
    // size of their own files.
    const chip = page.locator('.feasibility-chip');
    await expect(chip).toHaveClass(/feasibility-chip--ok/);
    await expect(chip.locator('ft-pill')).toHaveText('Fattibile');
    await expect(chip).toContainText('Richiesto 200 B');
    await expect(chip).toContainText('di margine');

    // A preview is a question, not a decision: nothing was queued and nothing moved.
    expect(await api.jobs()).toHaveLength(0);
    expect(await sandbox.exists('foto', 'foto-0001.jpg')).toBe(true);
  });
});

/**
 * Two operations on one file: a move that the destination refuses, and a rename that has to wait
 * for it. Both are real jobs in real states — the first one ran and was stopped by the disk, the
 * second was stopped by the first.
 */
async function twoJobsOnTheSameFile(
  api: Api,
  sandbox: Sandbox,
): Promise<{ blocked: Job; dependent: Job }> {
  const seeded = await sandbox.seed();
  // The destination, and the entry already sitting under the name the file is moving to.
  await sandbox.makeDir('archivio', 'foto-0001.jpg');
  const volume = await watchAndScanSandbox(api, sandbox, seeded.fileCount);

  const { files } = await api.walkCatalog(volume.id);
  const victim = files.find((f) => f.name === 'foto-0001.jpg')!;

  const blocked = await api.enqueue(
    {
      type: 'MoveFile',
      sourceFileId: victim.id,
      targetVolumeId: volume.id,
      targetRelativePath: sandbox.relative('archivio'),
    },
    sandbox.fence,
  );
  // Waiting for the block before enqueuing the second one is not synchronisation for its own sake:
  // it is what makes the second job's reason the interesting one. A guard that saw a job still
  // Pending would produce the same DependencyPending, but the first row would then be racing
  // towards a state the assertions are about.
  await api.waitForJobState(blocked.id, 'Blocked');

  const dependent = await api.enqueue(
    { type: 'RenameFile', sourceFileId: victim.id, newName: 'rinominato.jpg' },
    sandbox.fence,
  );
  expect(dependent.state).toBe('Blocked');
  expect(dependent.blockReason).toBe('DependencyPending');
  expect(dependent.dependsOnJobId).toBe(blocked.id);

  return { blocked, dependent };
}
