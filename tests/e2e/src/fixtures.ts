import { test as base } from '@playwright/test';

import { Api } from './api.js';
import type { JobRequestLike, SandboxFence } from './fence.js';
import { HostProcess } from './host.js';
import { Sandbox } from './sandbox.js';

/** The two routes that create work. Everything else the SPA posts only reads or annotates. */
const ENQUEUE_PATHS = new Set(['/api/operations/enqueue', '/api/operations/enqueue-batch']);

/**
 * The fence's verdict on what the SPA is about to post. A batch is a JSON array, a single enqueue
 * is one object; a body that is neither is refused rather than waved through, because a request
 * this cannot read is a request whose destination it cannot check.
 */
function enqueueViolation(fence: SandboxFence, body: unknown): string | null {
  const requests: unknown[] = Array.isArray(body) ? body : [body];
  if (requests.length === 0 || requests.some((r) => typeof r !== 'object' || r === null)) {
    return `unreadable enqueue body: ${JSON.stringify(body)}`;
  }
  for (const request of requests as JobRequestLike[]) {
    const violation = fence.violationOf(request);
    if (violation !== null) {
      return `${violation} (${JSON.stringify(request)})`;
    }
  }
  return null;
}

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

  api: async ({ host, sandbox }, use) => {
    const api = await Api.create(host);
    await use(api);

    // Layer 3 of the fence, and the last thing that happens while the Host is still answering:
    // every job the service recorded is read back and checked for containment. Layers 1 and 2
    // promise that nothing can leave the sandbox; this is the only one that reads what the engine
    // was actually told to do, so it runs whatever the test did or did not assert.
    try {
      sandbox.fence.auditRecordedJobs(await api.jobs());
    } finally {
      await api.dispose();
    }
  },

  /**
   * Two rules over every request the browser makes.
   *
   * The first is step 12a's: the product is a loopback service and its end-to-end proof must not
   * depend on the internet (index.html links a font stylesheet on fonts.googleapis.com), so
   * anything not addressed to this test's own Host is refused.
   *
   * The second is the fence. A spec that drives the move picker enqueues through the SPA, not
   * through `Api`, so the containment check has to sit where both paths meet: here, before the
   * request reaches the Host and therefore before the engine can act on it. A clean request is
   * passed through untouched — nothing is faked, and a refusal fails the test with the path that
   * caused it instead of quietly correcting the destination.
   */
  context: async ({ context, host, sandbox }, use) => {
    await context.route('**/*', (route) => {
      const request = route.request();
      const url = new URL(request.url());
      const ownHost = new URL(host.baseURL);
      if (url.host !== ownHost.host) {
        return route.abort();
      }

      if (request.method() === 'POST' && ENQUEUE_PATHS.has(url.pathname)) {
        const violation = enqueueViolation(sandbox.fence, request.postDataJSON());
        if (violation !== null) {
          sandbox.fence.recordBrowserViolation(violation);
          return route.abort('blockedbyclient');
        }
      }

      return route.continue();
    });

    await use(context);

    const violations = sandbox.fence.takeBrowserViolations();
    if (violations.length > 0) {
      throw new Error(
        `[SANDBOX FENCE] the screen tried to enqueue work outside the sandbox:\n  ${violations.join('\n  ')}`,
      );
    }
  },

  baseURL: async ({ host }, use) => {
    await use(host.baseURL);
  },
});

export { expect } from '@playwright/test';
