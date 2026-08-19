import { APIRequestContext, expect, request } from '@playwright/test';

import { HostProcess } from './host.js';

/** The slice of `VolumeDto` these tests reason about. */
export interface Volume {
  readonly id: number;
  readonly volumeGuid: string;
  readonly label: string | null;
  readonly currentLetter: string | null;
  readonly fileSystem: string;
  readonly isOnline: boolean;
  readonly isCatalogable: boolean;
  readonly fileCount: number;
  readonly lastFullScanUtc: string | null;
}

export interface WatchedRoot {
  readonly id: number;
  readonly relativePath: string;
  readonly isActive: boolean;
  readonly effectiveFilter: string;
}

export interface VolumeDetail extends Volume {
  readonly watchedRoots: readonly WatchedRoot[];
  readonly directoryCount: number;
  readonly indexedBytes: number;
}

export interface DashboardStats {
  readonly totalFiles: number;
  readonly totalBytes: number;
  readonly volumesOnline: number;
  readonly volumesTotal: number;
  readonly queuedJobs: number;
  readonly blockedJobs: number;
  readonly runningJobs: number;
  readonly pendingBytes: number;
}

/**
 * Setup and observation through the same HTTP API the SPA uses, carrying the same token the
 * browser was handed. It exists so a test that is *about* the Volumi screen does not have to
 * drive six other screens to arrive there — never to assert in place of the UI: what a screen
 * shows is asserted on the screen.
 */
export class Api {
  private constructor(
    private readonly ctx: APIRequestContext,
    readonly token: string,
  ) {}

  static async create(host: HostProcess): Promise<Api> {
    const token = await host.token();
    const ctx = await request.newContext({
      baseURL: host.baseURL,
      extraHTTPHeaders: { 'X-FileTracert-Token': token },
    });
    return new Api(ctx, token);
  }

  async dispose(): Promise<void> {
    await this.ctx.dispose();
  }

  async volumes(): Promise<Volume[]> {
    const response = await this.ctx.get('/api/volumes');
    expect(response.ok(), `GET /api/volumes → ${response.status()}`).toBeTruthy();
    return response.json();
  }

  async volume(id: number): Promise<VolumeDetail> {
    const response = await this.ctx.get(`/api/volumes/${id}`);
    expect(response.ok(), `GET /api/volumes/${id} → ${response.status()}`).toBeTruthy();
    return response.json();
  }

  async dashboard(): Promise<DashboardStats> {
    const response = await this.ctx.get('/api/dashboard');
    expect(response.ok(), `GET /api/dashboard → ${response.status()}`).toBeTruthy();
    return response.json();
  }

  /**
   * The volume the sandbox lives on, found by its current mount letter. The startup sweep of
   * VolumeSyncWorker runs as the Host boots, so this polls until the sweep has landed.
   */
  async volumeForDrive(driveRoot: string): Promise<Volume> {
    const letter = driveRoot.slice(0, 2).toUpperCase();
    let found: Volume | undefined;

    await expect
      .poll(
        async () => {
          found = (await this.volumes()).find((v) => v.currentLetter?.toUpperCase() === letter);
          return found !== undefined;
        },
        { message: `no volume mounted on ${letter} was catalogued`, timeout: 30_000 },
      )
      .toBe(true);

    return found!;
  }

  /**
   * Makes a volume catalogable. The sandbox sits on the system volume, which the classifier
   * excludes by default (§4) — the same override the Volumi screen offers as "Riabilita".
   */
  async setCatalogable(volumeId: number, isCatalogable: boolean): Promise<void> {
    const response = await this.ctx.post(`/api/volumes/${volumeId}/catalogable`, {
      data: { isCatalogable },
    });
    expect(response.status(), 'POST catalogable').toBe(204);
  }

  async addWatchedRoot(volumeId: number, relativePath: string): Promise<WatchedRoot> {
    const response = await this.ctx.post(`/api/volumes/${volumeId}/watched-roots`, {
      data: { relativePath, filterOverride: null },
    });
    expect(response.status(), `POST watched-roots → ${await response.text()}`).toBe(201);
    return response.json();
  }

  async requestRescan(volumeId: number): Promise<void> {
    const response = await this.ctx.post(`/api/volumes/${volumeId}/rescan`);
    expect(response.status(), 'POST rescan').toBe(202);
  }

  /**
   * Waits for a full scan of the volume to have finished and indexed exactly `expectedFiles`.
   * Polling a condition, not sleeping on a guess: the moment the catalog says what it should say,
   * the wait is over.
   */
  async waitForScan(volumeId: number, expectedFiles: number): Promise<void> {
    await expect
      .poll(async () => {
        const volume = await this.volume(volumeId);
        return volume.lastFullScanUtc === null ? -1 : volume.fileCount;
      }, {
        message: `volume ${volumeId} never finished a scan with ${expectedFiles} files`,
        timeout: 120_000,
      })
      .toBe(expectedFiles);
  }
}
