import { test as base } from '@playwright/test';

import { Api } from './api.js';
import type { JobRequestLike, SandboxFence } from './fence.js';
import { HostProcess } from './host.js';
import { Sandbox } from './sandbox.js';

/** The two routes that create work. Everything else the SPA posts only reads or annotates. */
const ENQUEUE_PATHS = new Set(['/api/operations/enqueue', '/api/operations/enqueue-batch']);

/** The route that widens what gets indexed, and therefore what an operation may name as a source. */
const WATCHED_ROOT_PATH = /^\/api\/volumes\/(\d+)\/watched-roots$/;

/** The POSTs whose body has to be read before it is allowed through. */
function isFencedPath(pathname: string): boolean {
  return ENQUEUE_PATHS.has(pathname) || WATCHED_ROOT_PATH.test(pathname);
}

/**
 * The fence's verdict on what the SPA is about to post. A body the fence cannot read is refused
 * rather than waved through: a request whose destination cannot be checked is a request that must
 * not be sent.
 */
function postViolation(fence: SandboxFence, pathname: string, body: unknown): string | null {
  const watchedRoot = WATCHED_ROOT_PATH.exec(pathname);
  if (watchedRoot !== null) {
    const relativePath = (body as { relativePath?: unknown } | null)?.relativePath;
    return typeof relativePath === 'string'
      ? fence.watchedRootViolation(Number(watchedRoot[1]), relativePath)
      : `unreadable watched-root body: ${JSON.stringify(body)}`;
  }

  if (!ENQUEUE_PATHS.has(pathname)) {
    return null;
  }

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
 * Reads back every job the service recorded and checks where it was sent — the fence's third
 * layer, and the only one that looks at what the engine was actually told to do rather than at
 * what a test asked for.
 *
 * A service that cannot be asked is never taken for a pass: the failure says so, and says it
 * without the `[SANDBOX FENCE]` prefix, which is reserved for work that really did go somewhere
 * it should not (a spec that stops the Host on purpose would otherwise raise a containment alarm
 * about a connection).
 */
async function auditWhatTheServiceRecorded(host: HostProcess, sandbox: Sandbox): Promise<void> {
  const api = await Api.create(host);
  try {
    let recorded;
    try {
      recorded = await api.jobs();
    } catch (error) {
      throw new Error(
        `[e2e] the containment audit could not run: the service did not answer (${String(error)}). ` +
          `Nothing here can say whether the queued work stayed inside ${sandbox.filesDir}.`,
      );
    }
    sandbox.fence.auditRecordedJobs(recorded);
  } finally {
    await api.dispose();
  }
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
      try {
        // Layer 3 of the fence, and the last thing that happens while the service still answers.
        // It hangs off the Host rather than off `api` on purpose: a spec that never asks for the
        // `api` fixture would otherwise queue work — through the browser, or through an HTTP
        // client of its own — with nothing reading back where that work was sent.
        await auditWhatTheServiceRecorded(host, sandbox);
      } finally {
        liveHosts.delete(host);
        await host.stop();
      }
    }
  },

  api: async ({ host }, use) => {
    const api = await Api.create(host);
    await use(api);
    await api.dispose();
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

      // Only the two POSTs that can reach outside are read: the SPA sends others with no body at
      // all (a rescan, a notification marked read), and asking those for JSON would throw.
      if (request.method() === 'POST' && isFencedPath(url.pathname)) {
        // A body that cannot be parsed is a destination that cannot be read; it is refused by
        // name rather than left to stall the request until the test times out.
        let body: unknown;
        try {
          body = request.postDataJSON();
        } catch (error) {
          sandbox.fence.recordBrowserViolation(
            `unreadable body on ${url.pathname}: ${String(error)}`,
          );
          return route.abort('blockedbyclient');
        }

        const violation = postViolation(sandbox.fence, url.pathname, body);
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
