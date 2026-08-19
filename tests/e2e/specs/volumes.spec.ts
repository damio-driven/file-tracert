import { expect, test } from '../src/fixtures.js';
import { watchAndScanSandbox, watchSandbox } from '../src/scenario.js';
import {
  detailValue,
  expandFolder,
  volumeListRow,
  watchFolder,
  watchedRootRows,
} from '../src/screens.js';

/**
 * The Volumi screen and the Setup screen it hands the user to. The volumes are the machine's real
 * ones — the Host talks to the real platform — so the assertions are about the volume the sandbox
 * lives on, found by its mount letter, and about facts the test itself established.
 */
test.describe('Volumi', () => {
  test("il dettaglio mostra l'identità del volume e le cartelle monitorate", async ({
    page,
    sandbox,
    api,
  }) => {
    const volume = await watchSandbox(api, sandbox);
    const letter = sandbox.driveRoot.slice(0, 2);

    await page.goto('/volumes');

    const row = volumeListRow(page, letter);
    await expect(row).toHaveCount(1);
    await row.click();
    await expect(row).toHaveClass(/sel/);

    // Identity is the Volume GUID, not the letter — the letter is only in the caption.
    await expect(detailValue(page, 'Volume GUID')).toHaveText(
      /^\\\\\?\\Volume\{[0-9a-fA-F-]{36}\}\\$/,
    );
    await expect(detailValue(page, 'Serial / Filesystem')).toContainText(volume.fileSystem);
    await expect(page.locator('.detail ft-panel .head .caption')).toContainText(
      `montato su ${letter}`,
    );

    // The root registered a moment ago is the one the panel lists.
    await expect(detailValue(page, 'Cartelle monitorate')).toHaveText(sandbox.volumeRelativePath);
    await expect(detailValue(page, 'Filtro tipi file')).toContainText('Tutti i tipi');
    await expect(detailValue(page, 'Ultima scansione')).toContainText('mai');
  });

  test("si aggiunge una cartella monitorata dall'albero del Setup", async ({
    page,
    sandbox,
    api,
  }) => {
    const volume = await api.volumeForDrive(sandbox.driveRoot);
    await api.setCatalogable(volume.id, true);

    await page.goto(`/setup?volume=${volume.id}`);
    await expect(watchedRootRows(page)).toHaveCount(0);

    // Walk the real filesystem tree down to the sandbox, one lazy level at a time.
    const segments = sandbox.pathSegments;
    for (const folder of segments.slice(0, -1)) {
      await expandFolder(page, folder).click();
    }
    await watchFolder(page, segments[segments.length - 1]!).click();

    const roots = watchedRootRows(page);
    await expect(roots).toHaveCount(1);
    await expect(roots.first()).toContainText(sandbox.volumeRelativePath);
    await expect(roots.first().getByText('Attiva', { exact: true })).toBeVisible();

    // And the Volumi screen, which reads it back from the service, agrees.
    await page.goto('/volumes');
    await volumeListRow(page, sandbox.driveRoot.slice(0, 2)).click();
    await expect(detailValue(page, 'Cartelle monitorate')).toHaveText(sandbox.volumeRelativePath);
  });

  test("cambiare il filtro dalla UI riallinea l'indice senza ri-scansionare", async ({
    page,
    sandbox,
    api,
  }) => {
    const seeded = await sandbox.seed();
    const volume = await watchAndScanSandbox(api, sandbox, seeded.fileCount);

    await page.goto(`/setup?volume=${volume.id}`);

    // "Immagini" alone: the five .jpg stay in, the four .txt and three .mp3 drop out.
    await page.getByRole('button', { name: 'Immagini', exact: true }).click();
    await page.getByRole('button', { name: 'Salva filtro', exact: true }).click();

    const note = page.locator('.ft-note');
    await expect(note).toHaveText(
      `Indice riallineato: ${seeded.imageCount} file inclusi · ${seeded.fileCount - seeded.imageCount} esclusi.`,
    );
    // Narrowing a filter cannot need a scan: nothing new has to be read off the disk.
    await expect(note).not.toHaveClass(/warn/);

    await page.goto('/volumes');
    await volumeListRow(page, sandbox.driveRoot.slice(0, 2)).click();
    await expect(detailValue(page, 'Filtro tipi file')).toContainText('Immagini');
    await expect(detailValue(page, 'Indice')).toContainText(`${seeded.imageCount} file inclusi`);
  });
});
