import { expect, test } from '../src/fixtures.js';
import { watchAndScanSandbox, watchSandbox } from '../src/scenario.js';
import {
  detailValue,
  expandFolder,
  selectVolume,
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
    await watchSandbox(api, sandbox);
    const letter = sandbox.driveRoot.slice(0, 2);

    await page.goto('/volumes');

    await expect(volumeListRow(page, letter)).toHaveCount(1);
    // Selecting waits for the detail panel to name this volume: everything below is about it.
    await selectVolume(page, letter);
    await expect(volumeListRow(page, letter)).toHaveClass(/sel/);

    // Identity is the Volume GUID, not the letter — the letter is only in the caption.
    await expect(detailValue(page, 'Volume GUID')).toHaveText(
      /^\\\\\?\\Volume\{[0-9a-fA-F-]{36}\}\\$/,
    );
    // Not compared with the value the API returned — that is the same source the screen read,
    // and two copies of one mistake agree. A filesystem name Windows can actually mount is what
    // there is to check without asking the product what it thinks it saw.
    await expect(detailValue(page, 'Serial / Filesystem')).toHaveText(
      /·\s(NTFS|ReFS|exFAT|FAT32|FAT)\s·/,
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
    // This spec puts the folder under watch through the *screen*, so it never goes through
    // `watchSandbox` — but the fence still has to know which volume is the sandbox's, or it would
    // refuse the very request the test is here to make.
    sandbox.fence.bindVolume(volume.id);

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
    // The pill, not the text: the row also carries a button labelled "Attiva" — the one offered
    // when the root is suspended — so plain text would be satisfied by the opposite state.
    await expect(roots.first().locator('ft-pill')).toHaveText('Attiva');

    // And the Volumi screen, which reads it back from the service, agrees.
    await page.goto('/volumes');
    await selectVolume(page, sandbox.driveRoot.slice(0, 2));
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
    await selectVolume(page, sandbox.driveRoot.slice(0, 2));
    await expect(detailValue(page, 'Filtro tipi file')).toContainText('Immagini');
    await expect(detailValue(page, 'Indice')).toContainText(`${seeded.imageCount} file inclusi`);
  });
});
