import { defineConfig, devices } from '@playwright/test';

/**
 * End-to-end configuration for the assembled product.
 *
 * There is no `webServer` here on purpose: every test gets its own Host process over its own
 * throwaway database (see `src/fixtures.ts`), which a single shared server could not give.
 * The build that those Hosts serve is produced once, by `globalSetup`.
 */
export default defineConfig({
  testDir: './specs',
  globalSetup: './src/global-setup.ts',

  // One Host binds one port and one database at a time. Parallelism here would buy seconds and
  // cost determinism, which is the opposite of the trade this level of the pyramid exists to make.
  fullyParallel: false,
  workers: 1,

  // Zero. A retry hides exactly the class of defect an end-to-end test is here to find: the one
  // that only shows up on some runs. A flaky test is a failing test.
  retries: 0,

  forbidOnly: true,

  // A test boots a Host, migrates a database and may scan thousands of real files.
  timeout: 180_000,
  expect: { timeout: 20_000 },

  reporter: [['list'], ['html', { outputFolder: '.artifacts/report', open: 'never' }]],
  outputDir: './.artifacts/test-results',

  use: {
    // baseURL is supplied per test by the `host` fixture: each Host listens on its own port.
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'off',
    actionTimeout: 20_000,
    navigationTimeout: 30_000,
  },

  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
});
