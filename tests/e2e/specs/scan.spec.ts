import { expect, test } from '../src/fixtures.js';
import type { TreeSpec } from '../src/sandbox.js';
import { watchSandbox } from '../src/scenario.js';
import {
  appearanceOf,
  dashboardCard,
  detailValue,
  latchAppearances,
  scanFlag,
  volumeListRow,
} from '../src/screens.js';

/**
 * A tree big enough that the scan is a visible event rather than an instant. The progress flag is
 * pushed over SignalR and by nothing else — step 10c deleted the polling timers — so a scan that
 * finished before the browser could be told anything would prove nothing about the socket.
 */
const BUSY_TREE: TreeSpec = {
  fileSizeBytes: 64,
  folders: Array.from({ length: 12 }, (_, i) => ({
    name: `lotto-${String(i + 1).padStart(2, '0')}`,
    files: 800,
    extension: '.jpg',
  })),
};

test.describe('Scansione', () => {
  test("parte dalla UI, l'avanzamento arriva dall'hub e i contatori finiscono aggiornati", async ({
    page,
    sandbox,
    api,
  }) => {
    const seeded = await sandbox.seed(BUSY_TREE);
    await watchSandbox(api, sandbox);
    const letter = sandbox.driveRoot.slice(0, 2);

    await page.goto('/volumes');
    const row = volumeListRow(page, letter);
    await row.click();
    await expect(detailValue(page, 'Ultima scansione')).toContainText('mai');

    // Armed before the click: progress is a state the product leaves on its own, and polling for
    // it would be a race against the scan finishing rather than a test of it starting.
    // The selected row swaps its metadata line for a progress bar, which is why it is watched
    // through the selection and not through that line any more.
    const FLAG = '.titlebar .scan-flag';
    const ROW_PROGRESS = 'li.vrow.sel ft-scan-progress';
    const DETAIL_PROGRESS = '.detail ft-scan-progress';
    await latchAppearances(page, [FLAG, ROW_PROGRESS, DETAIL_PROGRESS]);

    await page.getByRole('button', { name: '↻ Ri-scansiona' }).click();

    // Nothing was reloaded and nothing polls: this can only have arrived over the hub.
    expect((await appearanceOf(page, FLAG)).text).toContain('scansione in corso');
    expect((await appearanceOf(page, ROW_PROGRESS)).seen).toBe(true);
    expect((await appearanceOf(page, DETAIL_PROGRESS)).seen).toBe(true);

    // The terminal frame is what takes the flag away — a socket that simply died would leave it up.
    await expect(scanFlag(page)).toBeHidden({ timeout: 120_000 });

    // What the scan wrote is what the sandbox holds.
    await api.waitForScan((await api.volumeForDrive(sandbox.driveRoot)).id, seeded.fileCount);

    await page.goto('/volumes');
    await volumeListRow(page, letter).click();
    await expect(detailValue(page, 'Indice')).toContainText(
      `${seeded.fileCount.toLocaleString('it-IT')} file inclusi`,
    );
    await expect(detailValue(page, 'Ultima scansione')).not.toContainText('mai');

    // The card headline is a compact figure whose rounding is the browser's ICU business; the
    // byte total under it is not, and it is 9600 × 64 B to the byte. (The exact file count is
    // asserted digit for digit on the Dashboard in dashboard.spec.ts, on a small tree.)
    await page.goto('/dashboard');
    await expect(dashboardCard(page, 'File catalogati').locator('.meta')).toContainText('614,4 KB');
  });
});
