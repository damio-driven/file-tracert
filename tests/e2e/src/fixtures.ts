import { test as base } from '@playwright/test';

import { Api } from './api.js';
import { HostProcess } from './host.js';
import { Sandbox } from './sandbox.js';

/**
 * Every Host this process started, so a run that is interrupted (Ctrl+C, a crashing reporter)
 * does not leave a service holding a port and a database behind.
 */
const liveHosts = new Set<HostProcess>();

function killEveryHost(): void {
  for (const host of liveHosts) {
    host.forceKill();
  }
}

// `forceKill` is synchronous because an `exit` handler never gets to run anything it queues,
// and the signals are registered as well: on Windows Ctrl+C does not reliably raise `exit`.
process.on('exit', killEveryHost);
for (const signal of ['SIGINT', 'SIGTERM', 'SIGHUP', 'SIGBREAK'] as const) {
  process.on(signal, () => {
    killEveryHost();
    process.exit(1);
  });
}

export interface HostFixtures {
  /** The throwaway folder tree this test may touch — and the only one it may touch. */
  sandbox: Sandbox;
  /** A real Host process, serving the built SPA over a database created for this test alone. */
  host: HostProcess;
  /** The same HTTP API the SPA uses, with the same token the browser was given. */
  api: Api;
}

function slugFor(titlePath: string[], repeatEachIndex: number): string {
  const title = titlePath.slice(1).join('-') || 'test';
  const safe = title
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 32);
  return `${safe}-${repeatEachIndex}-${Math.random().toString(36).slice(2, 7)}`;
}

export const test = base.extend<HostFixtures>({
  sandbox: async ({}, use, testInfo) => {
    const sandbox = await Sandbox.create(slugFor(testInfo.titlePath, testInfo.repeatEachIndex));
    await use(sandbox);
    // Runs after the Host has stopped: nothing holds the database open any more, so a folder
    // that refuses to go is a handle the product failed to release, not a race with teardown.
    await sandbox.dispose();
  },

  host: async ({ sandbox }, use, testInfo) => {
    const host = await HostProcess.start({ workDir: sandbox.workDir });
    liveHosts.add(host);
    try {
      await use(host);
    } finally {
      if (testInfo.status !== testInfo.expectedStatus) {
        // A failing test is about to be reported; attach what the service was saying.
        await testInfo.attach('host.log', { body: await host.readLog(), contentType: 'text/plain' });
      }
      liveHosts.delete(host);
      await host.stop();
    }
  },

  api: async ({ host }, use) => {
    const api = await Api.create(host);
    await use(api);
    await api.dispose();
  },

  /**
   * The product is a loopback service and its end-to-end proof must not depend on the internet.
   * index.html links a font stylesheet on fonts.googleapis.com; left alone it makes every page
   * load as slow (or as failed) as the network happens to be. Anything not addressed to this
   * test's own Host is refused.
   */
  context: async ({ context, host }, use) => {
    await context.route('**/*', (route) => {
      const url = new URL(route.request().url());
      const ownHost = new URL(host.baseURL);
      return url.host === ownHost.host ? route.continue() : route.abort();
    });
    await use(context);
  },

  baseURL: async ({ host }, use) => {
    await use(host.baseURL);
  },
});

export { expect } from '@playwright/test';
