import { expect, test } from '../src/fixtures.js';
import { watchAndScanSandbox } from '../src/scenario.js';
import {
  bell,
  bellBadge,
  catalogVolume,
  connectionStatus,
  fileRow,
  jobRow,
  jobState,
  notificationPanel,
  openFolder,
  openPath,
} from '../src/screens.js';

/**
 * The push, end to end — the gap steps 10b and 10c wrote down and left for this level.
 *
 * Since step 10c the screens have no timers at all: they read once when they are opened and are
 * patched by the hub afterwards. So every change asserted here is triggered from **outside** the
 * browser, through the HTTP API, and the browser is never told to reload. If the socket were not
 * carrying the message, the screen would simply stay as it was.
 *
 * Nothing is intercepted or faked: that is what the Vitest suite does, with a `HubConnection`
 * stand-in. Here it is the real WebSocket to `/hubs/events` on the real Host.
 *
 * One thing this level still cannot show: the **progress bar of a copy**. `JobProgress` is emitted
 * while bytes are being copied, which only happens on a cross-volume move — and a second volume
 * would mean writing outside the sandbox. The equivalent motion is asserted below on the states a
 * job goes through, and the scan's own progress bar is covered in `scan.spec.ts`.
 */
test.describe('Realtime', () => {
  test('la Coda vede comparire e finire un job accodato da fuori', async ({
    page,
    sandbox,
    api,
  }) => {
    const seeded = await sandbox.seed();
    await sandbox.makeDir('archivio');
    const volume = await watchAndScanSandbox(api, sandbox, seeded.fileCount);

    await page.goto('/queue');
    await expect(page.locator('.empty-title')).toHaveText('Nessuna operazione in coda');

    const { files } = await api.walkCatalog(volume.id);
    const job = await api.enqueue(
      {
        type: 'MoveFile',
        sourceFileId: files.find((f) => f.name === 'foto-0003.jpg')!.id,
        targetVolumeId: volume.id,
        targetRelativePath: sandbox.relative('archivio'),
      },
      sandbox.fence,
    );

    // The browser did nothing: no navigation, no click, no timer. The row can only be here
    // because the hub said so.
    await expect(jobRow(page, job.id)).toBeVisible();
    await expect(jobState(page, job.id)).toHaveText('Completato');
    await expect(page.locator('.stat-chip--done')).toContainText('1');

    expect(await sandbox.exists('archivio', 'foto-0003.jpg')).toBe(true);
  });

  test('il Catalogo riceve la proiezione senza ricaricare', async ({ page, sandbox, api }) => {
    const seeded = await sandbox.seed();
    // The destination, holding the name the file is about to take: the job parks Blocked and the
    // overlay stays, so what the screen receives is a projection that holds still.
    await sandbox.makeDir('archivio', 'foto-0001.jpg');
    const volume = await watchAndScanSandbox(api, sandbox, seeded.fileCount);

    await page.goto('/catalog');
    await catalogVolume(page, sandbox.driveRoot.slice(0, 2)).click();
    await openPath(page, sandbox.pathSegments);
    await openFolder(page, 'archivio');
    await expect(page.locator('tr.file-row')).toHaveCount(0);

    const { files } = await api.walkCatalog(volume.id);
    await api.enqueue(
      {
        type: 'MoveFile',
        sourceFileId: files.find((f) => f.name === 'foto-0001.jpg')!.id,
        targetVolumeId: volume.id,
        targetRelativePath: sandbox.relative('archivio'),
      },
      sandbox.fence,
    );

    // `ProjectionChanged` is the only thing that can have moved this screen: the enqueue happened
    // in another process entirely, and the Catalogo has not re-read anything on its own since
    // step 10c took its timers away.
    const arrived = fileRow(page, 'foto-0001.jpg');
    await expect(arrived).toHaveCount(1);
    await expect(arrived.locator('a.badge')).toHaveText('In spostamento');
  });

  test('la campanella si accende su un errore di background', async ({ page, sandbox, api }) => {
    const seeded = await sandbox.seed();
    await sandbox.makeDir('archivio', 'foto-0001.jpg');
    const volume = await watchAndScanSandbox(api, sandbox, seeded.fileCount);

    // A scan on an NTFS volume without elevation already raises one notice (the USN journal it
    // cannot open), so what matters is the increment, not the number. The badge is absent rather
    // than zero when there is nothing to read, which is the right design and has to be read as
    // such here.
    const before = await api.unreadNotifications();

    await page.goto('/dashboard');
    if (before === 0) {
      await expect(bellBadge(page)).toHaveCount(0);
    } else {
      await expect(bellBadge(page)).toHaveText(String(before));
    }

    const { files } = await api.walkCatalog(volume.id);
    await api.enqueue(
      {
        type: 'MoveFile',
        sourceFileId: files.find((f) => f.name === 'foto-0001.jpg')!.id,
        targetVolumeId: volume.id,
        targetRelativePath: sandbox.relative('archivio'),
      },
      sandbox.fence,
    );

    // The engine runs, cannot finish, and says so out loud. The badge moves with nothing reloaded:
    // that increment is `NotificationRaised` arriving.
    await expect(bellBadge(page)).toHaveText(String(before + 1));

    // The panel's contents come from a real read when it opens — the push carries only the fact
    // that there is something new — so what is asserted here is that the something is the block.
    await bell(page).click();
    const newest = notificationPanel(page).locator('.ft-notif-item').first();
    await expect(newest.locator('.ft-notif-itemtitle')).toHaveText(
      'Operazione MoveFile bloccata (NameCollision)',
    );
    await expect(newest.locator('ft-pill')).toHaveText('Avviso');
  });

  test('a connessione persa la shell lo dice, e al ritorno le schermate rileggono', async ({
    page,
    context,
    host,
    sandbox,
    api,
  }) => {
    const seeded = await sandbox.seed();
    await sandbox.makeDir('archivio');
    const volume = await watchAndScanSandbox(api, sandbox, seeded.fileCount);
    const { files } = await api.walkCatalog(volume.id);

    await page.goto('/queue');
    await expect(page.locator('.empty-title')).toHaveText('Nessuna operazione in coda');
    // Healthy is silent: a badge that is always lit says nothing, so its absence is the assertion.
    await expect(connectionStatus(page)).toHaveCount(0);

    // The service goes down the way it does in life — the whole Host, with its own shutdown
    // sequence. No stub and no intercepted frame: the socket is closed by the server that owned it.
    await host.stop();
    await expect(connectionStatus(page)).toBeVisible();
    await expect(connectionStatus(page)).toContainText('riconnessione');

    // SignalR's own ramp runs out after about forty seconds; then the shell stops promising and
    // offers the button instead.
    await expect(connectionStatus(page)).toHaveClass(/conn--offline/, { timeout: 90_000 });
    await expect(connectionStatus(page)).toContainText('aggiornamenti in pausa');
    const reconnect = connectionStatus(page).getByRole('button', { name: 'Riconnetti' });
    await expect(reconnect).toBeVisible();

    // From here the client would retry on its own every fifteen seconds, which would make what
    // happens next a race. Holding the browser offline removes it: the service comes back, work
    // happens, and the page cannot have heard about any of it.
    await context.setOffline(true);
    await host.restart();

    const job = await api.enqueue(
      {
        type: 'MoveFile',
        sourceFileId: files.find((f) => f.name === 'foto-0004.jpg')!.id,
        targetVolumeId: volume.id,
        targetRelativePath: sandbox.relative('archivio'),
      },
      sandbox.fence,
    );
    await api.waitForJobState(job.id, 'Completed');
    // The `JobStateChanged` for it was broadcast to nobody and is not replayed: a client that
    // reconnected in silence would keep showing an empty queue for good.
    await expect(page.locator('.empty-title')).toHaveText('Nessuna operazione in coda');

    await context.setOffline(false);
    await reconnect.click();

    // The indicator goes away because the connection came back; the row appears because the
    // recovery re-reads what is on screen instead of waiting for messages that are already lost.
    await expect(connectionStatus(page)).toHaveCount(0);
    await expect(jobRow(page, job.id)).toBeVisible();
    await expect(jobState(page, job.id)).toHaveText('Completato');
  });
});
