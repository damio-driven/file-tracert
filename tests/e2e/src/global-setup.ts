import { execFile, spawnSync } from 'node:child_process';
import { access, mkdir, rm } from 'node:fs/promises';
import path from 'node:path';
import { promisify } from 'node:util';

import { artifactsRoot, frontendDir, hostExePath, hostProjectDir, repoRoot } from './paths.js';

const execFileAsync = promisify(execFile);

const BUILD_TIMEOUT_MS = 15 * 60_000;

/** SID of the High Mandatory Level integrity group: present in an elevated token, absent otherwise. */
const HIGH_INTEGRITY_SID = 'S-1-16-12288';

/**
 * Builds what the tests will run against, once. The browser talks to the Host serving the built
 * SPA from `wwwroot` — same origin, token from the `<meta>` tag, WebSocket straight to the hub —
 * so both halves have to be current before the first test starts. `FT_E2E_SKIP_BUILD=1` skips it
 * when you have just built by hand and are iterating on a spec.
 *
 * Note: if a Host is already running from this working copy, the backend build fails on a locked
 * DLL. Close it first (the repository's own rule).
 */
export default async function globalSetup(): Promise<void> {
  refuseElevation();

  await rm(artifactsRoot, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  await mkdir(artifactsRoot, { recursive: true });

  if (process.env['FT_E2E_SKIP_BUILD'] === '1') {
    await assertBuilt();
    return;
  }

  await run('dotnet', ['build', hostProjectDir, '-v:m', '--nologo'], repoRoot);
  await run('npm.cmd', ['run', 'build'], frontendDir);
  await assertBuilt();
}

/**
 * Refuses to run elevated, and says why.
 *
 * The scan picks its engine from the filesystem, not from a setting: on NTFS it always *tries*
 * the USN journal, and `EnsureJournal` creates one when none is active. Elevated, that is a
 * persistent change to the developer's system volume made by a test — outside the sandbox this
 * suite promises never to leave — and the snapshot read walks the whole MFT, which turns the scan
 * into a function of the size of that disk. Non-elevated the same call is refused by Windows and
 * the product falls back to enumeration of the watched root, which is the only thing here that
 * stays inside the sandbox.
 */
function refuseElevation(): void {
  const groups = spawnSync('whoami', ['/groups'], { windowsHide: true, encoding: 'utf8' });
  if (groups.error !== undefined || typeof groups.stdout !== 'string') {
    // Cannot tell; do not invent a verdict either way.
    return;
  }
  if (groups.stdout.includes(HIGH_INTEGRITY_SID)) {
    throw new Error(
      'These tests must not run elevated: a scan of an NTFS volume would try (and be allowed) ' +
        'to create a USN journal on the system volume and to walk its whole MFT — outside the ' +
        'sandbox, and unbounded in time. Run them from a normal terminal.',
    );
  }
}

async function assertBuilt(): Promise<void> {
  for (const artifact of [hostExePath, path.join(hostProjectDir, 'wwwroot', 'index.html')]) {
    try {
      await access(artifact);
    } catch {
      throw new Error(`Missing build artifact: ${artifact}. Run without FT_E2E_SKIP_BUILD=1.`);
    }
  }
}

async function run(command: string, args: string[], cwd: string): Promise<void> {
  const started = Date.now();
  process.stdout.write(`[e2e] ${command} ${args.join(' ')}\n`);
  try {
    // No shell: the repository path is passed as an argument and a shell would re-split it on
    // spaces, failing the build with an error about a path nobody wrote.
    await execFileAsync(command, args, { cwd, timeout: BUILD_TIMEOUT_MS, windowsHide: true });
  } catch (error) {
    const details = error as { stdout?: string; stderr?: string };
    throw new Error(
      `[e2e] "${command} ${args.join(' ')}" failed in ${cwd}\n${details.stdout ?? ''}\n${details.stderr ?? ''}`,
    );
  }
  process.stdout.write(`[e2e] done in ${((Date.now() - started) / 1000).toFixed(1)}s\n`);
}
