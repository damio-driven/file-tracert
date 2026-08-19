import { expect, type Locator, type Page } from '@playwright/test';

/**
 * The handful of places two specs both need to point at. Deliberately small: a page object per
 * screen would be more structure than four flows can pay for, and every locator here is the text
 * or the role a user would use to find the same thing.
 */

/** The shell's own verdict on the service, always present in the titlebar. */
export const serviceTray = (page: Page): Locator => page.locator('.titlebar__right .tray');

/**
 * The titlebar flag that says a scan is running. It is driven by `ScanProgress` over SignalR and
 * by nothing else — step 10c removed the polling timers — so its appearance is the push arriving.
 */
export const scanFlag = (page: Page): Locator => page.locator('.titlebar .scan-flag');

/** One Dashboard card, found by the label the user reads on it. */
export const dashboardCard = (page: Page, key: string): Locator =>
  page.locator('.cards ft-card').filter({ has: page.locator('.key', { hasText: key }) });

/** A row of the Dashboard volumes table, found by the drive letter shown on it. */
export const dashboardVolumeRow = (page: Page, letter: string): Locator =>
  page.locator('table.ft-table tbody tr').filter({ has: page.getByText(letter, { exact: true }) });

/** A row of the Volumi list, found by the drive letter in its metadata line. */
export const volumeListRow = (page: Page, letter: string): Locator =>
  page.locator('li.vrow').filter({ hasText: `${letter} ·` });

/** The caption of the Volumi detail panel: it names the mount point of the selected volume. */
export const detailCaption = (page: Page): Locator =>
  page.locator('.detail ft-panel .head .caption');

/**
 * Selects a volume on Volumi and does not return until the detail panel is showing *that* volume.
 *
 * The wait before the click is not decoration. The screen auto-selects the first catalogable
 * volume as soon as the list arrives, and that selection is applied when its own request comes
 * back — which, on a busy machine, can be after the one the click sent. The later response wins,
 * the selection silently moves to another volume, and every assertion that follows is about the
 * wrong row. Letting the automatic selection land first removes the race from the test; the
 * product keeps it (see the notes for step 12a).
 */
export async function selectVolume(page: Page, letter: string): Promise<void> {
  await expect(detailCaption(page)).toBeVisible();
  await volumeListRow(page, letter).click();
  await expect(detailCaption(page)).toContainText(`montato su ${letter}`);
}

/** The value cell of one key/value row of the Volumi detail panel. */
export const detailValue = (page: Page, label: string): Locator =>
  page
    .locator('table.ft-table--kv tr')
    .filter({ has: page.locator('td', { hasText: new RegExp(`^${label}$`) }) })
    .locator('td')
    .nth(1);

/** The Setup folder tree's expander for a folder, by the accessible name it carries. */
export const expandFolder = (page: Page, name: string): Locator =>
  page.getByRole('button', { name: `Espandi ${name}`, exact: true });

/** The Setup folder tree's "+ Monitora" button for a folder. */
export const watchFolder = (page: Page, name: string): Locator =>
  page.getByRole('button', { name: `Monitora la cartella ${name}`, exact: true });

/** The list of monitored roots on the Setup screen. */
export const watchedRootRows = (page: Page): Locator => page.locator('ul.roots li.root');

// ── Catalogo ──────────────────────────────────────────────────────────────────────────────────

/** The Catalogo's volume button, found by the mount letter in its metadata line. */
export const catalogVolume = (page: Page, letter: string): Locator =>
  page.locator('button.vol-item').filter({ hasText: letter });

/** One folder card of the Catalogo, found by the name printed on it. */
export const folderCard = (page: Page, name: string): Locator =>
  page.locator('.dir-card').filter({ has: page.locator('.dir-name', { hasText: exact(name) }) });

/** One file row of the Catalogo, found by the name printed on it. */
export const fileRow = (page: Page, name: string): Locator =>
  page.locator('tr.file-row').filter({ has: page.locator('.file-name', { hasText: exact(name) }) });

/**
 * Opens one folder of the Catalogo and waits until the breadcrumb says we are in it.
 *
 * Waiting on the breadcrumb rather than on the card disappearing is what makes a walk down several
 * levels safe: each level is its own request, and clicking the next card before the previous
 * answer landed would click a card that is about to be replaced.
 */
export async function openFolder(page: Page, name: string): Promise<void> {
  await folderCard(page, name).locator('.dir-card-open').click();
  await expect(page.locator('.breadcrumb-seg--current')).toHaveText(name);
}

/** One breadcrumb of the Catalogo, to walk back up without reloading anything. */
export const crumb = (page: Page, name: string): Locator =>
  page.locator('.breadcrumb-seg', { hasText: exact(name) });

/** Walks the Catalogo from the volume root down to the sandbox, one lazy level at a time. */
export async function openPath(page: Page, segments: readonly string[]): Promise<void> {
  for (const segment of segments) {
    await openFolder(page, segment);
  }
}

// ── Picker (Sposta selezionati…) ───────────────────────────────────────────────────────────────

/** The move picker dialog. */
export const picker = (page: Page): Locator => page.getByRole('dialog', { name: 'Sposta file' });

/**
 * Points the picker at a destination folder: the volume, then one click per level.
 *
 * The volume is chosen by index because the option's value is an Angular `ngValue` token, not the
 * volume id, and its label is assembled from three interpolations — the letter is the one part
 * that identifies the volume to a person reading the list.
 */
export async function pickDestination(
  page: Page,
  letter: string,
  segments: readonly string[],
): Promise<void> {
  const dialog = picker(page);
  const select = dialog.locator('#picker-vol');
  const index = await select.evaluate(
    (element, wanted) =>
      Array.from((element as HTMLSelectElement).options).findIndex((option) =>
        option.text.includes(`(${wanted})`),
      ),
    letter,
  );
  expect(index, `no destination volume mounted on ${letter}`).toBeGreaterThan(0);
  await select.selectOption({ index });

  // The folder list is the same lazy catalog the Catalogo screen browses, so each level waits for
  // the crumb the answer produced before the next click.
  for (const segment of segments) {
    await dialog
      .locator('button.folder-row')
      // On the name, not on the whole row: the row also prints how much the folder holds.
      .filter({ has: page.locator('.folder-row-name', { hasText: exact(segment) }) })
      .click();
    await expect(dialog.locator('.folder-crumb.active')).toHaveText(segment);
  }
}

// ── Coda ──────────────────────────────────────────────────────────────────────────────────────

/** One job row of the Coda, by the id the API gave the job. */
export const jobRow = (page: Page, jobId: number): Locator => page.locator(`tr#job-${jobId}`);

/** The state pill of a job row: "In attesa", "Bloccato", "Completato"… */
export const jobState = (page: Page, jobId: number): Locator =>
  jobRow(page, jobId).locator('ft-pill');

/** The explanation the Coda prints under a blocked job. */
export const jobBlockDetail = (page: Page, jobId: number): Locator =>
  jobRow(page, jobId).locator('.block-detail');

// ── Shell: campanella e stato della connessione ────────────────────────────────────────────────

/** The unread badge on the notification bell. Absent, not empty, when there is nothing to read. */
export const bellBadge = (page: Page): Locator => page.locator('.ft-notif-badge');

export const bell = (page: Page): Locator => page.getByRole('button', { name: /^Notifiche/ });

/** The bell's panel. It is portaled to the body, so panel locators are scoped to it, not to the header. */
export const notificationPanel = (page: Page): Locator =>
  page.getByRole('dialog', { name: 'Notifiche' });

/**
 * The shell's connection indicator. It is deliberately absent while the socket is healthy — a
 * badge that is always on says nothing — so its presence is the assertion, not its text.
 */
export const connectionStatus = (page: Page): Locator => page.locator('.titlebar__right .conn');

/** A locator that matches the whole text and nothing more of it. */
function exact(text: string): RegExp {
  return new RegExp(`^\\s*${text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\s*$`);
}

/** What the page recorded for one watched selector: whether it ever appeared, and its first text. */
export interface Appearance {
  readonly seen: boolean;
  readonly text: string;
}

/**
 * Records the appearance of elements that are only there while something is happening.
 *
 * A progress indicator is a state the product enters and leaves on its own. Asserting it with a
 * poll makes the test a race against the work it started: fast enough, and the whole event fits
 * between two polls and the assertion fails without anything being wrong. So the browser is asked
 * to remember instead — a MutationObserver installed *before* the action latches the first
 * sighting of each selector, and the assertion reads the latch, which cannot expire.
 */
export async function latchAppearances(page: Page, selectors: string[]): Promise<void> {
  await page.evaluate((watched) => {
    const seen: Record<string, string> = {};
    (window as unknown as { __ftSeen: Record<string, string> }).__ftSeen = seen;

    const sweep = (): void => {
      for (const selector of watched) {
        if (selector in seen) {
          continue;
        }
        const element = document.querySelector(selector);
        if (element !== null) {
          seen[selector] = (element.textContent ?? '').replace(/\s+/g, ' ').trim();
        }
      }
    };

    sweep();
    new MutationObserver(sweep).observe(document.documentElement, {
      subtree: true,
      childList: true,
      characterData: true,
    });
  }, selectors);
}

/** Waits until the latch has recorded this selector, and returns what it recorded. */
export async function appearanceOf(page: Page, selector: string): Promise<Appearance> {
  await expect
    .poll(
      () =>
        page.evaluate(
          (s) => s in (window as unknown as { __ftSeen: Record<string, string> }).__ftSeen,
          selector,
        ),
      { message: `"${selector}" never appeared` },
    )
    .toBe(true);

  const text = await page.evaluate(
    (s) => (window as unknown as { __ftSeen: Record<string, string> }).__ftSeen[s] ?? '',
    selector,
  );
  return { seen: true, text };
}
