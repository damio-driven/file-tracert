import { expect, test } from '../src/fixtures.js';
import { watchAndScanSandbox } from '../src/scenario.js';
import {
  catalogVolume,
  crumb,
  fileRow,
  jobRow,
  jobState,
  openFolder,
  openPath,
  picker,
  pickDestination,
} from '../src/screens.js';

/**
 * §5, on the screen: the Catalogo is not a photograph of the disk but the disk *plus* the overlay
 * of what is queued. Queue a move and the file is already at the destination, with a badge — and
 * the Ricerca finds it there too, because the name the index carries is the projected one.
 *
 * The first test needs that intermediate state to hold still long enough to be looked at. It does
 * not stop the queue worker to get it: the destination already holds an entry with the name the
 * file is moving to, so the engine runs, hits a real collision and parks the job `Blocked` — which
 * §5 says **keeps** the overlay. The colliding entry is a *folder* on purpose, so that the file
 * list at the destination has exactly one row with that name and an assertion cannot be satisfied
 * by the wrong one.
 *
 * The second test is the other half of the promise: when the operation really runs, the projection
 * becomes the disk, and the badge goes away without anybody reloading anything.
 */
test.describe('Proiezione', () => {
  test("accodare uno spostamento sposta subito il file, con il badge e il link alla Coda", async ({
    page,
    sandbox,
    api,
  }) => {
    const seeded = await sandbox.seed();
    // The destination, and the entry that will make the move stop there instead of finishing.
    await sandbox.makeDir('archivio', 'foto-0001.jpg');
    await watchAndScanSandbox(api, sandbox, seeded.fileCount);
    const letter = sandbox.driveRoot.slice(0, 2);

    await page.goto('/catalog');
    await catalogVolume(page, letter).click();
    await openPath(page, sandbox.pathSegments);
    await openFolder(page, 'foto');

    await page.getByLabel('Seleziona foto-0001.jpg').check();
    await expect(page.locator('.selection-count')).toContainText('1 file selezionato');
    await page.getByRole('button', { name: 'Sposta selezionati…' }).click();

    await pickDestination(page, letter, [...sandbox.pathSegments, 'archivio']);
    await page.getByRole('button', { name: 'Accoda →' }).click();
    await expect(picker(page).locator('.enqueued-title')).toHaveText('1 operazione accodata');
    await picker(page).getByRole('button', { name: 'Chiudi' }).click();

    const job = (await api.jobs())[0]!;
    expect(job.type).toBe('MoveFile');

    // Nothing was reloaded. The file has left the folder it is still physically in, because the
    // catalog lists by projected position.
    await expect(fileRow(page, 'foto-0001.jpg')).toHaveCount(0);
    await expect(page.locator('tr.file-row')).toHaveCount(seeded.imageCount - 1);
    expect(await sandbox.exists('foto', 'foto-0001.jpg')).toBe(true);

    // And it is already at the destination, wearing what it is waiting for.
    await crumb(page, 'files').click();
    await openFolder(page, 'archivio');
    const moved = fileRow(page, 'foto-0001.jpg');
    await expect(moved).toHaveCount(1);
    await expect(moved.locator('a.badge')).toHaveText('In spostamento');
    await expect(moved.locator('a.badge')).toHaveAttribute('href', `/queue?job=${job.id}`);

    // The Ricerca reads the same projection: the projected name is what the index carries, and the
    // path it shows is resolved through the overlay, not read off the row.
    await page.goto('/search');
    await page.getByPlaceholder(/Cerca per nome/).fill('foto-0001');
    await page.getByRole('button', { name: 'Cerca', exact: true }).click();
    const result = page.locator('tr.result-row');
    await expect(result).toHaveCount(1);
    await expect(result.locator('.col-path')).toContainText(
      `${sandbox.volumeRelativePath}\\archivio`,
    );
    await expect(result.locator('a.badge')).toHaveText('In spostamento');

    // The badge is a way in, not decoration: it opens the Coda on that job's row.
    await result.locator('a.badge').click();
    await expect(page).toHaveURL(new RegExp(`/queue\\?job=${job.id}$`));
    await expect(jobRow(page, job.id)).toHaveClass(/job-row--focused/);

    // The job is where the engine left it: stopped on a name that is already taken, said in words,
    // with the one button that can move it on.
    await expect(jobState(page, job.id)).toHaveText('Bloccato');
    await expect(jobRow(page, job.id).locator('.block-reason')).toHaveText('Conflitto di nome');
    await expect(jobRow(page, job.id).getByRole('button', { name: 'Riprova' })).toBeVisible();
  });

  test('a operazione completata la proiezione diventa il disco', async ({ page, sandbox, api }) => {
    const seeded = await sandbox.seed();
    await sandbox.makeDir('archivio');
    const volume = await watchAndScanSandbox(api, sandbox, seeded.fileCount);
    const letter = sandbox.driveRoot.slice(0, 2);

    await page.goto('/catalog');
    await catalogVolume(page, letter).click();
    await openPath(page, sandbox.pathSegments);
    await openFolder(page, 'foto');

    await page.getByLabel('Seleziona foto-0002.jpg').check();
    await page.getByRole('button', { name: 'Sposta selezionati…' }).click();
    await pickDestination(page, letter, [...sandbox.pathSegments, 'archivio']);
    await page.getByRole('button', { name: 'Accoda →' }).click();
    await expect(picker(page).locator('.enqueued-title')).toHaveText('1 operazione accodata');
    await picker(page).getByRole('button', { name: 'Chiudi' }).click();

    const job = (await api.jobs())[0]!;
    await api.waitForJobState(job.id, 'Completed');

    // The disk is what the projection promised. This is the assertion the whole checkpoint is for:
    // everything above it is what the screens said, this is what actually happened.
    expect(await sandbox.exists('archivio', 'foto-0002.jpg')).toBe(true);
    expect(await sandbox.exists('foto', 'foto-0002.jpg')).toBe(false);

    // And the screen says the same, without a reload: the badge is gone because the overlay was
    // cleared in the same transaction that completed the job, and the Catalogo heard about it.
    await crumb(page, 'files').click();
    await openFolder(page, 'archivio');
    const moved = fileRow(page, 'foto-0002.jpg');
    await expect(moved).toHaveCount(1);
    await expect(moved.locator('.badge')).toHaveCount(0);

    await page.goto(`/queue?job=${job.id}`);
    await expect(jobState(page, job.id)).toHaveText('Completato');
    await expect(jobRow(page, job.id).locator('.col-paths')).toContainText('archivio');

    // The catalog agrees with the disk down to the count: nothing was duplicated on the way.
    const { files } = await api.walkCatalog(volume.id);
    expect(files.filter((f) => f.name === 'foto-0002.jpg').map((f) => f.relativePath)).toEqual([
      sandbox.relative('archivio', 'foto-0002.jpg'),
    ]);
  });
});
