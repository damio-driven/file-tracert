import { execFile, spawn } from 'node:child_process';
import { openSync, closeSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import path from 'node:path';

import { hostExePath, hostProjectDir, startHostScript, stopHostScript } from './paths.js';
import { reserveHostPort } from './ports.js';

/** Placeholder the Host replaces with the live token when it serves index.html (SpaEndpoints). */
const TOKEN_PLACEHOLDER = '__FT_TOKEN__';

export interface HostOptions {
  /** Directory that will hold the throwaway database and the Host's log. */
  readonly workDir: string;
  /**
   * Seconds between automatic scan sweeps. The default is an hour so that a scan only ever
   * happens because a test asked for one: with the shipped 30 s the worker would pick a freshly
   * added watched root up on its own and race the click the test is trying to prove.
   */
  readonly scanPollIntervalSeconds?: number;
  /** Seconds between volume re-syncs. The sweep at startup happens regardless of this value. */
  readonly volumeSyncIntervalSeconds?: number;
}

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
  } catch {
    return false;
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
        '-VolumeSyncIntervalSeconds', String(options.volumeSyncIntervalSeconds ?? 3600),
        '-ScanPollIntervalSeconds', String(options.scanPollIntervalSeconds ?? 3600),
      ],
      launcherLog,
    );

    const pid = Number.parseInt(await readFile(pidPath, 'utf8').catch(() => ''), 10);
    if (exitCode !== 0 || !Number.isInteger(pid) || pid <= 0) {
      const details = await readFile(launcherLog, 'utf8').catch(() => '(no launcher output)');
      throw new Error(`start-host.ps1 exited with ${exitCode} and no usable pid.\n${details}`);
    }

    const host = new HostProcess(pid, port, `http://127.0.0.1:${port}`, databasePath, logPath);
    await host.waitUntilServing();
    return host;
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
    while (Date.now() < deadline) {
      if (!isAlive(this.pid)) {
        return;
      }
      await sleep(POLL_INTERVAL_MS);
    }

    const log = await this.readLog();
    this.forceKill();
    throw new Error(
      `The Host (pid ${this.pid}) did not stop within ${STOP_TIMEOUT_MS} ms` +
        (sendFailure === null ? '' : ` (Ctrl+Break could not be delivered: ${sendFailure})`) +
        `.\n${log}`,
    );
  }

  /** Last resort, used when the graceful stop failed and when the whole run is torn down. */
  forceKill(): void {
    if (!isAlive(this.pid)) {
      return;
    }
    try {
      execFile('taskkill', ['/PID', String(this.pid), '/T', '/F'], { windowsHide: true });
    } catch {
      // Nothing left to try; the failure that brought us here is the one being reported.
    }
  }
}
