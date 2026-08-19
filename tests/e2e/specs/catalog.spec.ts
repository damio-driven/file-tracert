import { expect, test } from '../src/fixtures.js';
import { watchAndScanSandbox } from '../src/scenario.js';
import {
  catalogVolume,
  detailValue,
  fileRow,
  folderCard,
  openFolder,
  openPath,
  selectVolume,
} from '../src/screens.js';

/**
 * The Catalogo, over the index a real scan of the sandbox produced.
 *
 * The screen browses one level per request, and the sandbox sits several levels down a real
 * volume, so walking to it is not scaffolding — it is the lazy tree being exercised the only way
 * that proves it: by asking for a level the previous answer did not contain.
 */
test.describe('Catalogo', () => {
  test("naviga l'albero lazy e conta ciò che la sandbox contiene", async ({
    page,
    sandbox,
    api,
  }) => {
    const seeded = await sandbox.seed();
    // One level deeper than the seeded tree, so a folder has both a subfolder and files of its own
    // and the two counters on its card cannot be the same number by accident.
    await sandbox.writeFileOfSize(200, 'foto', '2026', 'estate-0001.jpg');
    await watchAndScanSandbox(api, sandbox, seeded.fileCount + 1);

    await page.goto('/catalog');

    // Nothing is chosen for the user: the Catalogo waits to be told which volume to open.
    await expect(page.locator('.empty-title')).toHaveText('Seleziona un volume');

    await catalogVolume(page, sandbox.driveRoot.slice(0, 2)).click();
    await openPath(page, sandbox.pathSegments);

    // Three folders, and the counters say what the test itself put on disk.
    await expect(page.locator('.dir-card')).toHaveCount(3);
    const foto = folderCard(page, 'foto').locator('.dir-counts');
    await expect(foto).toContainText('1 cartelle');
    await expect(foto).toContainText('5 file');
    await expect(folderCard(page, 'musica').locator('.dir-counts')).toContainText('3 file');

    // The sandbox folder holds no files of its own: everything is in the three subfolders.
    await expect(page.locator('tr.file-row')).toHaveCount(0);

    await openFolder(page, 'foto');
    await expect(page.locator('.section-count')).toHaveText('(5)');
    await expect(page.locator('tr.file-row')).toHaveCount(5);
    await expect(fileRow(page, 'foto-0001.jpg')).toBeVisible();
    // 200 B written by the test, rendered by the byte pipe.
    await expect(fileRow(page, 'foto-0001.jpg').locator('.col-size')).toHaveText('200 B');
    // The image tag, not a generic one: the category is derived at indexing time from the
    // extension and persisted, and this is where a wrong mapping would show.
    await expect(fileRow(page, 'foto-0001.jpg').locator('.cat-tag')).toHaveText('IMG');

    // And the subfolder is reachable from here, which is the level the first request did not have.
    await openFolder(page, '2026');
    await expect(fileRow(page, 'estate-0001.jpg')).toBeVisible();

    // Back up through the breadcrumb, to the folder that is now two crumbs behind.
    await page.locator('.breadcrumb-seg', { hasText: 'foto' }).click();
    await expect(page.locator('tr.file-row')).toHaveCount(5);
  });

  test("una cartella esclusa dal filtro resta nell'albero, con i suoi file esclusi", async ({
    page,
    sandbox,
    api,
  }) => {
    const seeded = await sandbox.seed();
    const volume = await watchAndScanSandbox(api, sandbox, seeded.fileCount);
    const letter = sandbox.driveRoot.slice(0, 2);

    // How many folders the volume holds, read off the screen before anything is narrowed. The
    // absolute number depends on how deep this checkout sits, so what is asserted is that it does
    // not move — which is the whole claim of step 11h.
    await page.goto('/volumes');
    await selectVolume(page, letter);
    const structureBefore = await detailValue(page, 'Struttura').innerText();
    expect(structureBefore).toContain("cartelle nell'albero");

    // Narrow the filter to images through the product, from the screen that offers it.
    await page.goto(`/setup?volume=${volume.id}`);
    await page.getByRole('button', { name: 'Immagini', exact: true }).click();
    await page.getByRole('button', { name: 'Salva filtro', exact: true }).click();
    await expect(page.locator('.ft-note')).toHaveText(
      `Indice riallineato: ${seeded.imageCount} file inclusi · ${seeded.fileCount - seeded.imageCount} esclusi.`,
    );

    await page.goto('/catalog');
    await catalogVolume(page, letter).click();
    await openPath(page, sandbox.pathSegments);

    // The folder whose files are all excluded is still there — excluded is not absent — and it
    // says so by counting no files rather than by disappearing.
    await expect(page.locator('.dir-card')).toHaveCount(3);
    await expect(folderCard(page, 'documenti').locator('.dir-counts')).toHaveText('Vuota');
    await expect(folderCard(page, 'foto').locator('.dir-counts')).toContainText('5 file');

    // And it can still be opened: the tree kept the structure the disk has.
    await openFolder(page, 'documenti');
    await expect(page.locator('.empty-title')).toHaveText('Nessun file indicizzato');

    // The two figures on Volumi now describe two different perimeters, and only one of them moved.
    await page.goto('/volumes');
    await selectVolume(page, letter);
    await expect(detailValue(page, 'Indice')).toContainText(`${seeded.imageCount} file inclusi`);
    expect(await detailValue(page, 'Struttura').innerText()).toBe(structureBefore);
  });
});
