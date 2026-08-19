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
