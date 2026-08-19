import { spawn, spawnSync } from 'node:child_process';
import { openSync, closeSync } from 'node:fs';
import { access, readFile } from 'node:fs/promises';
import path from 'node:path';

import { hostExePath, hostProjectDir, startHostScript, stopHostScript } from './paths.js';
import { reserveHostPort } from './ports.js';

/** Placeholder the Host replaces with the live token when it serves index.html (SpaEndpoints). */
const TOKEN_PLACEHOLDER = '__FT_TOKEN__';

export interface HostOptions {
  /** Directory that will hold the throwaway database and the Host's log. */
  readonly workDir: string;
}

/**
 * Seconds between automatic scan sweeps, and between volume re-syncs. An hour, so that a scan
 * only ever happens because a test asked for one: with the shipped 30 s the worker would pick a
 * freshly added watched root up on its own and race the click the test is trying to prove. The
 * sweeps that run once at startup happen regardless.
 */
const IDLE_INTERVAL_SECONDS = 3600;

/** How long a Host may take to answer its first request. */
const START_TIMEOUT_MS = 60_000;
/** How long a Host may take to stop after being asked. Its own shutdown budget is 30 s. */
const STOP_TIMEOUT_MS = 45_000;
const POLL_INTERVAL_MS = 200;

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

function isAlive(pid: number): boolean {
  try {
    // Signal 0 does not deliver anything; it only asks whether the process is there.
    process.kill(pid, 0);
    return true;
  } catch (error) {
    // EPERM means "there, but not yours to signal" — the opposite of gone. Reading it as gone
    // would make the teardown accuse the Host of having exited on its own.
    return (error as NodeJS.ErrnoException).code === 'EPERM';
  }
}

/**
 * Runs one of the helper scripts and resolves with its exit code.
 *
 * Its output goes to a file, never to a pipe. `Start-Process` hands the Host every inheritable
 * handle the launcher holds, so a pipe here would stay open for as long as the Host lives, and
 * waiting for the launcher would mean waiting for the service it just started.
 */
function runPowerShell(script: string, args: string[], logPath: string): Promise<number> {
  const fd = openSync(logPath, 'a');
  try {
    const child = spawn(
      'powershell.exe',
      ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', script, ...args],
      { windowsHide: true, stdio: ['ignore', fd, fd] },
    );
    return new Promise<number>((resolve, reject) => {
      child.once('error', reject);
      child.once('exit', (code) => resolve(code ?? -1));
    }).finally(() => closeSync(fd));
  } catch (error) {
    closeSync(fd);
    throw error;
  }
}

/**
 * One real Host process: the product, serving the built SPA over a database that exists only for
 * the test that started it.
 */
export class HostProcess {
  private constructor(
    readonly pid: number,
    readonly port: number,
    readonly baseURL: string,
    readonly databasePath: string,
    readonly logPath: string,
  ) {}

  private cachedToken: string | null = null;
  private stopped = false;

  static async start(options: HostOptions): Promise<HostProcess> {
    const port = await reserveHostPort();
    const databasePath = path.join(options.workDir, 'db', 'filetracert.db');
    const logPath = path.join(options.workDir, 'host.log');
    const pidPath = path.join(options.workDir, 'host.pid');
    const launcherLog = path.join(options.workDir, 'launcher.log');

    const exitCode = await runPowerShell(
      startHostScript,
      [
        '-ExePath', hostExePath,
        '-WorkingDirectory', hostProjectDir,
        '-LogPath', logPath,
        '-PidPath', pidPath,
        '-Port', String(port),
        '-DatabasePath', databasePath,
        '-VolumeSyncIntervalSeconds', String(IDLE_INTERVAL_SECONDS),
        '-ScanPollIntervalSeconds', String(IDLE_INTERVAL_SECONDS),
      ],
      launcherLog,
    );

    const pid = Number.parseInt(await readFile(pidPath, 'utf8').catch(() => ''), 10);
    if (exitCode !== 0 || !Number.isInteger(pid) || pid <= 0) {
      const details = await readFile(launcherLog, 'utf8').catch(() => '(no launcher output)');
      throw new Error(`start-host.ps1 exited with ${exitCode} and no usable pid.\n${details}`);
    }

    const host = new HostProcess(pid, port, `http://127.0.0.1:${port}`, databasePath, logPath);
    try {
      await host.waitUntilServing();
      await host.assertUsingThrowawayDatabase();
    } catch (error) {
      // The process is running and nobody is holding it yet: left alone it would keep the port
      // and keep the database open, so the sandbox could not be deleted and the next run would
      // start on a dirty artifacts folder.
      host.forceKill();
      throw error;
    }
    return host;
  }

  /**
   * The isolation rule with an assertion behind it. If the database path ever failed to reach the
   * Host, `DatabaseLocation.Resolve` would silently fall back to %LOCALAPPDATA%\FileTracert —
   * the user's real catalog — and migrate it. Checking that the file this Host was told to create
   * exists turns that from a silent accident into an immediate, named failure.
   */
  private async assertUsingThrowawayDatabase(): Promise<void> {
    try {
      await access(this.databasePath);
    } catch {
      throw new Error(
        `The Host is serving but did not create ${this.databasePath}. It may be running on ` +
          `another database — refusing to continue.\n${await this.readLog()}`,
      );
    }
  }

  /**
   * Waits until Kestrel answers. `/health` is token-protected, so a 401 is a fully started Host:
   * the database has been migrated (that happens before the first request is served) and routing
   * is up. Anything else is still starting.
   */
  private async waitUntilServing(): Promise<void> {
    const deadline = Date.now() + START_TIMEOUT_MS;
    let lastError = 'no response';

    while (Date.now() < deadline) {
      if (!isAlive(this.pid)) {
        throw new Error(`The Host exited during startup.\n${await this.readLog()}`);
      }
      try {
        const response = await fetch(`${this.baseURL}/health`);
        if (response.status === 401 || response.status === 200) {
          return;
        }
        lastError = `unexpected status ${response.status}`;
      } catch (error) {
        lastError = String(error);
      }
      await sleep(POLL_INTERVAL_MS);
    }

    throw new Error(
      `The Host did not answer on ${this.baseURL} within ${START_TIMEOUT_MS} ms (${lastError}).\n` +
        (await this.readLog()),
    );
  }

  /**
   * The token the way the browser gets it: read out of the `<meta name="ft-token">` of the
   * index.html this Host serves. Reading it from the database instead would let the test keep
   * working with the stamping broken, which is the one thing this must not do.
   */
  async token(): Promise<string> {
    if (this.cachedToken !== null) {
      return this.cachedToken;
    }

    const response = await fetch(`${this.baseURL}/`);
    if (!response.ok) {
      throw new Error(`GET / returned ${response.status}; is the SPA built into wwwroot?`);
    }

    const html = await response.text();
    const match = /<meta\s+name="ft-token"\s+content="([^"]*)"/i.exec(html);
    if (match === null) {
      throw new Error('The served index.html carries no ft-token meta tag.');
    }
    if (match[1] === '' || match[1] === TOKEN_PLACEHOLDER) {
      throw new Error('The served index.html still carries the token placeholder, not a token.');
    }

    this.cachedToken = match[1];
    return this.cachedToken;
  }

  /** The Host's stdout and stderr, for failure messages. */
  async readLog(): Promise<string> {
    const parts: string[] = [];
    for (const [name, file] of [
      ['host.log', this.logPath],
      ['host.log.err', `${this.logPath}.err`],
    ] as const) {
      try {
        const text = await readFile(file, 'utf8');
        parts.push(`--- ${name} (tail) ---\n${text.split(/\r?\n/).slice(-40).join('\n')}`);
      } catch {
        parts.push(`--- ${name} unavailable ---`);
      }
    }
    return parts.join('\n');
  }

  /**
   * Asks the Host to shut down the way a console Ctrl+Break would, and waits for it to be gone.
   * Throws if it is still there afterwards: a Host that will not stop is a product defect (the
   * shutdown path of step 11c), not a teardown detail to be papered over — but it is force-killed
   * first, so one stuck Host cannot poison the rest of the run.
   */
  async stop(): Promise<void> {
    if (this.stopped) {
      return;
    }
    this.stopped = true;

    if (!isAlive(this.pid)) {
      throw new Error(`The Host exited on its own before the test ended.\n${await this.readLog()}`);
    }

    let sendFailure: string | null = null;
    try {
      const code = await runPowerShell(
        stopHostScript,
        ['-TargetPid', String(this.pid)],
        `${this.logPath}.stop`,
      );
      if (code !== 0) {
        sendFailure = `stop-host.ps1 exited with ${code}`;
      }
    } catch (error) {
      sendFailure = String(error);
    }

    const deadline = Date.now() + STOP_TIMEOUT_MS;
    let gone = false;
    while (!gone && Date.now() < deadline) {
      gone = !isAlive(this.pid);
      if (!gone) {
        await sleep(POLL_INTERVAL_MS);
      }
    }

    const log = await this.readLog();
    if (!gone) {
      this.forceKill();
      throw new Error(
        `The Host (pid ${this.pid}) did not stop within ${STOP_TIMEOUT_MS} ms` +
          (sendFailure === null ? '' : ` (Ctrl+C could not be delivered: ${sendFailure})`) +
          `.\n${log}`,
      );
    }

    // A Host that is gone is not proof of a graceful stop: if the event never reached it, it
    // died of something else and the shutdown path was never exercised. Reporting a clean stop
    // there would be the silence §9 forbids.
    if (sendFailure !== null) {
      throw new Error(
        `The Host stopped, but Ctrl+C was never delivered to it (${sendFailure}), ` +
          `so nothing here proves it shut down cleanly.\n${log}`,
      );
    }
  }

  /**
   * Last resort, used when the graceful stop failed and when the whole run is torn down.
   * Synchronous on purpose: it is called from a `process.on('exit')` handler, where anything
   * asynchronous is queued and then never runs.
   */
  forceKill(): void {
    if (!isAlive(this.pid)) {
      return;
    }
    const result = spawnSync('taskkill', ['/PID', String(this.pid), '/T', '/F'], {
      windowsHide: true,
    });
    if (result.error !== undefined || result.status !== 0) {
      // Nothing left to try, but a service that will not die is worth a line on the console.
      process.stderr.write(
        `[e2e] could not kill the Host (pid ${this.pid}): ` +
          `${result.error ?? `taskkill exited with ${result.status}`}\n`,
      );
    }
  }
}
