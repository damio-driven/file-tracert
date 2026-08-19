import { expect, test } from '../src/fixtures.js';
import { watchAndScanSandbox } from '../src/scenario.js';

/**
 * Ricerca, over the FTS5 index the same scan populated.
 *
 * Every number here is one the test wrote to disk: five .jpg in `foto`, four .txt in `documenti`,
 * three .mp3 in `musica`. Nothing is compared against a second reading of the API the screen used.
 *
 * Not covered on purpose: the **size** filters. They exist in the store and have no control on
 * this screen (a known limit, recorded in CLAUDE.md); inventing one here would test a screen the
 * product does not have.
 */
test.describe('Ricerca', () => {
  test('trova per nome, distingue nome da percorso e filtra per categoria', async ({
    page,
    sandbox,
    api,
  }) => {
    const seeded = await sandbox.seed();
    await watchAndScanSandbox(api, sandbox, seeded.fileCount);

    await page.goto('/search');
    await expect(page.locator('.empty-title')).toHaveText('Inizia la ricerca');

    const box = page.getByPlaceholder(/Cerca per nome/);
    // The scope buttons are `Nome`/`Percorso`, and the sort bar offers a `Nome` of its own: the
    // class is what tells them apart.
    const scope = (label: string) => page.locator('.scope-btn').filter({ hasText: label });
    const search = async (text: string): Promise<void> => {
      await box.fill(text);
      await page.getByRole('button', { name: 'Cerca', exact: true }).click();
    };

    // A prefix of the seeded file names: the tokenizer splits `foto-0001.jpg` on the punctuation,
    // so this is the name column answering, not a LIKE over the path.
    await search('foto');
    await expect(page.locator('.result-count')).toContainText(`${seeded.imageCount} risultati`);
    await expect(page.locator('tr.result-row')).toHaveCount(seeded.imageCount);
    await expect(page.locator('tr.result-row').first().locator('.col-path')).toContainText(
      `${sandbox.volumeRelativePath}\\foto`,
    );

    await search('documenti');
    await expect(page.locator('tr.result-row')).toHaveCount(4);
    await expect(page.locator('.result-count')).toContainText('4 risultati');

    // The two scopes are only distinguishable on a term that appears in the path and in no file
    // name: every seeded file is named after the folder it sits in, so the discriminating term is
    // a segment of the sandbox path itself. On "solo nome" the index has nothing to answer with…
    await scope('Nome').click();
    await search('.artifacts');
    await expect(page.locator('.empty-title')).toHaveText('Nessun risultato');

    // …and on "percorso completo" it answers with everything under that folder.
    await scope('Percorso').click();
    await expect(page.locator('tr.result-row')).toHaveCount(seeded.fileCount);

    // One numbered file per folder, then narrowed to the one that is an image. The category is
    // derived from the extension at indexing time, so this is the index answering too.
    await scope('Nome').click();
    await search('0001');
    await expect(page.locator('tr.result-row')).toHaveCount(3);

    await page.locator('button.chip').filter({ hasText: 'Immagine' }).click();
    await expect(page.locator('tr.result-row')).toHaveCount(1);
    await expect(page.locator('tr.result-row').locator('.file-name')).toHaveText('foto-0001.jpg');
  });

  test('il filtro data ragiona in giorni locali', async ({ page, sandbox, api }) => {
    const seeded = await sandbox.seed();
    await watchAndScanSandbox(api, sandbox, seeded.fileCount);

    await page.goto('/search');
    await page.getByPlaceholder(/Cerca per nome/).fill('foto');
    await page.getByRole('button', { name: 'Cerca', exact: true }).click();
    await expect(page.locator('tr.result-row')).toHaveCount(seeded.imageCount);

    // The files were written a moment ago, so "modified today" must include them: the local day
    // the user picked becomes an instant in UTC, and the upper bound runs to the last tick of it.
    const day = (offsetDays: number): string => {
      const date = new Date();
      date.setDate(date.getDate() + offsetDays);
      return [
        date.getFullYear(),
        String(date.getMonth() + 1).padStart(2, '0'),
        String(date.getDate()).padStart(2, '0'),
      ].join('-');
    };

    await page.getByLabel('Modificato dal giorno').fill(day(0));
    await page.getByLabel('Modificato fino al giorno').fill(day(0));
    await expect(page.locator('tr.result-row')).toHaveCount(seeded.imageCount);

    // And tomorrow excludes them, which is the half that fails if the bound is built from a naive
    // date rather than from the local day.
    await page.getByLabel('Modificato dal giorno').fill(day(1));
    await expect(page.locator('.empty-title')).toHaveText('Nessun risultato');

    await page.getByRole('button', { name: 'Rimuovi il filtro data' }).click();
    await expect(page.locator('tr.result-row')).toHaveCount(seeded.imageCount);
  });
});
