import { expect, test } from '../src/fixtures.js';
import { watchAndScanSandbox } from '../src/scenario.js';
import { dashboardCard, dashboardVolumeRow } from '../src/screens.js';

/**
 * The Dashboard states what the catalog holds. Every number asserted here is one the test put on
 * disk itself — twelve files of two hundred bytes — not one read back out of the same API the
 * screen used.
 */
test.describe('Dashboard', () => {
  test('su un catalogo vuoto le card dicono zero e la coda è ferma', async ({ page }) => {
    await page.goto('/dashboard');

    await expect(dashboardCard(page, 'File catalogati').locator('.value')).toHaveText('0');
    await expect(dashboardCard(page, 'File catalogati').locator('.meta')).toContainText('0 B');

    // The unit sits in a <small> inside the value, so the text carries both.
    const queue = dashboardCard(page, 'Coda');
    await expect(queue.locator('.value')).toHaveText(/^\s*0\s+task\s*$/);
    await expect(queue.locator('.meta')).toHaveText('nessuna operazione in coda');

    const waiting = dashboardCard(page, 'In attesa di spazio/volume');
    await expect(waiting.locator('.meta')).toHaveText('nessuna operazione ferma');
  });

  test('dopo una scansione le card contano i file della sandbox', async ({
    page,
    sandbox,
    api,
  }) => {
    const seeded = await sandbox.seed();
    await watchAndScanSandbox(api, sandbox, seeded.fileCount);

    await page.goto('/dashboard');

    const catalogued = dashboardCard(page, 'File catalogati');
    await expect(catalogued.locator('.value')).toHaveText(String(seeded.fileCount));
    // 12 × 200 B = 2400 B, which the byte pipe renders in decimal units with an Italian comma.
    await expect(catalogued.locator('.meta')).toHaveText(/^su \d+ volumi · 2,4 KB$/);

    // The volume the sandbox lives on is listed, online, with its file count.
    const letter = sandbox.driveRoot.slice(0, 2);
    const row = dashboardVolumeRow(page, letter);
    await expect(row).toHaveCount(1);
    await expect(row.getByText('Online')).toBeVisible();
    await expect(row.locator('td').last()).toHaveText(String(seeded.fileCount));

    // The shell footer repeats the same figure, and must agree with the card above it.
    await expect(page.locator('.navfoot')).toContainText(`${seeded.fileCount} file`);
  });
});
