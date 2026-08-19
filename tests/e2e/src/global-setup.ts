import { execFile } from 'node:child_process';
import { access, mkdir, rm } from 'node:fs/promises';
import { promisify } from 'node:util';

import { artifactsRoot, frontendDir, hostExePath, hostProjectDir, repoRoot } from './paths.js';

const execFileAsync = promisify(execFile);

const BUILD_TIMEOUT_MS = 15 * 60_000;

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
  await rm(artifactsRoot, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  await mkdir(artifactsRoot, { recursive: true });

  if (process.env['FT_E2E_SKIP_BUILD'] === '1') {
    await assertBuilt();
    return;
  }

  await run('dotnet', ['build', hostProjectDir, '-v:m', '--nologo'], repoRoot);
  await run('npm', ['run', 'build'], frontendDir);
  await assertBuilt();
}

async function assertBuilt(): Promise<void> {
  for (const artifact of [hostExePath, `${hostProjectDir}\\wwwroot\\index.html`]) {
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
    await execFileAsync(command, args, { cwd, timeout: BUILD_TIMEOUT_MS, shell: true, windowsHide: true });
  } catch (error) {
    const details = error as { stdout?: string; stderr?: string };
    throw new Error(
      `[e2e] "${command} ${args.join(' ')}" failed in ${cwd}\n${details.stdout ?? ''}\n${details.stderr ?? ''}`,
    );
  }
  process.stdout.write(`[e2e] done in ${((Date.now() - started) / 1000).toFixed(1)}s\n`);
}
