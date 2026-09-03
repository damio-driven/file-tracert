import { APIRequestContext, expect, request } from '@playwright/test';

import type { JobRequestLike, RecordedJob, SandboxFence } from './fence.js';
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

export interface CatalogDir {
  readonly id: number;
  readonly name: string;
  readonly materializedPath: string;
  readonly childDirectoryCount: number;
  readonly fileCount: number;
  readonly projectedState: string;
  readonly pendingJobId: number | null;
}

export interface CatalogFile {
  readonly id: number;
  readonly name: string;
  readonly sizeBytes: number;
  readonly projectedState: string;
  readonly pendingJobId: number | null;
}

export interface CatalogChildren {
  readonly directories: { readonly items: readonly CatalogDir[]; readonly totalCount: number };
  readonly files: { readonly items: readonly CatalogFile[]; readonly totalCount: number };
}

export interface Job extends RecordedJob {
  readonly state: string;
  readonly blockReason: string;
  readonly dependsOnJobId: number | null;
  readonly errorMessage: string | null;
  readonly sequenceOrder: number;
}

/** What a spec knows about one catalogued entry, keyed by the id the API uses for operations. */
export interface CataloguedEntry {
  readonly id: number;
  readonly name: string;
  /** Physical path relative to the volume root. */
  readonly relativePath: string;
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

  /**
   * Puts a folder under watch. Fenced like an enqueue, and for the same reason: what the scan
   * indexes is what an operation may name as a source.
   */
  async addWatchedRoot(
    volumeId: number,
    relativePath: string,
    fence: SandboxFence,
  ): Promise<WatchedRoot> {
    fence.assertWatchedRootStaysInside(volumeId, relativePath);
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

  async catalogChildren(
    volumeId: number,
    directoryId: number | null,
    dirSkip = 0,
  ): Promise<CatalogChildren> {
    const params = new URLSearchParams({ dirSkip: String(dirSkip) });
    if (directoryId !== null) params.set('directoryId', String(directoryId));
    const response = await this.ctx.get(`/api/catalog/${volumeId}/children?${params}`);
    expect(response.ok(), `GET catalog children → ${response.status()}`).toBeTruthy();
    return response.json();
  }

  /**
   * Every catalogued file and directory of a volume, with the path the index gives it.
   *
   * Walking the whole tree is affordable because the sandbox is tiny, and it is the honest way to
   * answer "where does id N live?": the answer comes from the catalog the operation will be
   * resolved against, not from the folder a test happens to have clicked on.
   */
  async walkCatalog(volumeId: number): Promise<{
    files: CataloguedEntry[];
    directories: CataloguedEntry[];
  }> {
    const files: CataloguedEntry[] = [];
    const directories: CataloguedEntry[] = [];

    const visit = async (directoryId: number | null, prefix: string): Promise<void> => {
      const children = await this.catalogChildren(volumeId, directoryId);
      for (const file of children.files.items) {
        files.push({ id: file.id, name: file.name, relativePath: join(prefix, file.name) });
      }
      // Subfolders are paged since step 17: the walk asks for every page, so the fence's
      // perimeter check (which relies on this walk) cannot stop at the first fifty.
      const subfolders = [...children.directories.items];
      while (subfolders.length < children.directories.totalCount) {
        const next = await this.catalogChildren(volumeId, directoryId, subfolders.length);
        if (next.directories.items.length === 0) break;
        subfolders.push(...next.directories.items);
      }
      for (const dir of subfolders) {
        directories.push({ id: dir.id, name: dir.name, relativePath: dir.materializedPath });
        await visit(dir.id, dir.materializedPath);
      }
    };

    await visit(null, '');
    return { files, directories };
  }

  /**
   * The only way a test can enqueue anything: the fence has to clear the request first, and the
   * fence can only come from the sandbox fixture. A spec that wants to reach `/api/operations`
   * without one has to add its own HTTP client, which is exactly the sort of thing a review reads.
   */
  async enqueue(request: JobRequestLike, fence: SandboxFence): Promise<Job> {
    fence.assertRequestStaysInside(request);
    const response = await this.ctx.post('/api/operations/enqueue', { data: request });
    expect(response.status(), `POST enqueue → ${await response.text()}`).toBe(201);
    return response.json();
  }

  /**
   * Every job in the queue. The count is asserted against what the endpoint says it holds, because
   * this is what the fence's audit reads: a page silently short of the whole truth would be a
   * containment check that missed the one job that mattered.
   */
  async jobs(): Promise<Job[]> {
    const response = await this.ctx.get('/api/operations?take=200');
    expect(response.ok(), `GET /api/operations → ${response.status()}`).toBeTruthy();
    const page = await response.json();
    expect(
      page.items.length,
      `the queue holds ${page.totalCount} jobs but one page carries ${page.items.length}`,
    ).toBe(page.totalCount);
    return page.items;
  }

  async job(id: number): Promise<Job> {
    const found = (await this.jobs()).find((j) => j.id === id);
    expect(found, `no job #${id} in the queue`).toBeDefined();
    return found!;
  }

  /** Waits until a job reaches one of the given states, and returns it. */
  async waitForJobState(id: number, ...states: string[]): Promise<Job> {
    let last = '';
    await expect
      .poll(
        async () => {
          last = (await this.job(id)).state;
          return states.includes(last);
        },
        { message: `job #${id} never reached ${states.join('/')} (last state: ${last})`, timeout: 60_000 },
      )
      .toBe(true);
    return this.job(id);
  }

  async unreadNotifications(): Promise<number> {
    const response = await this.ctx.get('/api/notifications/unread-count');
    expect(response.ok(), `GET unread-count → ${response.status()}`).toBeTruthy();
    return (await response.json()).unread;
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

/** Volume-relative path join, in the catalog's spelling. */
function join(prefix: string, name: string): string {
  return prefix.length === 0 ? name : `${prefix}\\${name}`;
}
