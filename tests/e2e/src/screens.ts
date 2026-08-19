import type { Locator, Page } from '@playwright/test';

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
