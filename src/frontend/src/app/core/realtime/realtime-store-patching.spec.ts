import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { vi } from 'vitest';

import { CatalogApi } from '../api/catalog-api.service';
import { NotificationsApi } from '../api/notifications-api.service';
import { QueueApi } from '../api/queue-api.service';
import { SearchApi } from '../api/search-api.service';
import { VolumesApi } from '../api/volumes-api.service';
import { CatalogStore } from '../../features/catalog/catalog.store';
import { NotificationsStore } from '../../features/notifications/notifications.store';
import { QueueStore } from '../../features/queue/queue.store';
import { SearchStore } from '../../features/search/search.store';
import { VolumesStore } from '../../features/volumes/volumes.store';
import {
  CatalogChildrenDto, OperationJobDto, PagedResult, SearchResultDto, VolumeDto,
} from '../models/catalog.models';

const job = (id: number, over: Partial<OperationJobDto> = {}): OperationJobDto => ({
  id,
  type: 'MoveFile',
  state: 'Copying',
  blockReason: 'None',
  sourceVolumeId: 1,
  sourceVolumeLabel: 'SSD',
  targetVolumeId: 2,
  targetVolumeLabel: 'USB',
  sourcePath: 'Docs\\file.txt',
  targetPath: 'Backup\\file.txt',
  isIntraVolume: false,
  totalBytes: 1000,
  bytesProcessed: 0,
  requiredBytesTarget: 1000,
  freedBytesSource: 0,
  estimateIsLive: true,
  sequenceOrder: id,
  dependsOnJobId: null,
  errorMessage: null,
  createdUtc: '2026-01-01T00:00:00Z',
  startedUtc: null,
  completedUtc: null,
  feasibility: null,
  ...over,
});

const page = <T>(items: T[]): PagedResult<T> => ({
  items, totalCount: items.length, skip: 0, take: 50,
});

describe('QueueStore fed by realtime', () => {
  afterEach(() => {
    vi.useRealTimers();
    TestBed.resetTestingModule();
  });

  function setup(jobs: OperationJobDto[]) {
    const list = vi.fn(() => of(page(jobs)));
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: QueueApi, useValue: { list } }],
    });
    return { store: TestBed.inject(QueueStore), list };
  }

  it('patches only the row a JobProgress names, and never reloads the list', async () => {
    const { store, list } = setup([job(1), job(2)]);
    await store.load();
    const untouched = store.jobs()[1];

    store.applyProgress({ jobId: 1, bytesProcessed: 512, totalBytes: 1000 });

    expect(store.jobs()[0].bytesProcessed).toBe(512);
    expect(store.jobs()[1]).toBe(untouched);
    expect(list).toHaveBeenCalledTimes(1);
  });

  it('patches state, block reason and error from JobStateChanged', async () => {
    const { store } = setup([job(1)]);
    await store.load();

    store.applyStateChanged({
      jobId: 1, state: 'Blocked', blockReason: 'InsufficientSpace', errorMessage: 'mancano 2 GB',
    });

    expect(store.jobs()[0].state).toBe('Blocked');
    expect(store.jobs()[0].blockReason).toBe('InsufficientSpace');
    expect(store.jobs()[0].errorMessage).toBe('mancano 2 GB');
    expect(store.blockedCount()).toBe(1);
  });

  it('survives a message about a job it has never loaded', () => {
    const { store, list } = setup([]);

    expect(() =>
      store.applyProgress({ jobId: 99, bytesProcessed: 1, totalBytes: 2 }),
    ).not.toThrow();
    expect(() =>
      store.applyStateChanged({
        jobId: 99, state: 'Pending', blockReason: 'None', errorMessage: null,
      }),
    ).not.toThrow();
    expect(list).not.toHaveBeenCalled();
  });

  it('reloads once, coalesced, when a state change names a job outside the page', async () => {
    vi.useFakeTimers();
    const { store, list } = setup([job(1)]);
    await store.load();

    store.applyStateChanged({
      jobId: 50, state: 'Pending', blockReason: 'None', errorMessage: null,
    });
    store.applyStateChanged({
      jobId: 51, state: 'Pending', blockReason: 'None', errorMessage: null,
    });
    await vi.advanceTimersByTimeAsync(1_000);

    expect(list).toHaveBeenCalledTimes(2);
  });
});

describe('VolumesStore fed by realtime', () => {
  afterEach(() => TestBed.resetTestingModule());

  const volume = (id: number): VolumeDto => ({
    id,
    volumeGuid: `Volume{${id}}`,
    label: 'Backup',
    currentLetter: 'E:',
    fileSystem: 'NTFS',
    isRemovable: true,
    isOnline: false,
    lastSeenUtc: '2026-08-01T00:00:00Z',
    capacityBytes: 1000,
    freeBytes: 100,
    fileCount: 5,
    lastFullScanUtc: null,
    dataIsLive: false,
    kind: 'Removable',
    isCatalogable: true,
  });

  it('flips the volume online and moves its freshness flags with it', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: VolumesApi, useValue: { list: () => of([volume(1), volume(2)]) } },
      ],
    });
    const store = TestBed.inject(VolumesStore);
    await store.loadList();

    store.applyVolumeStatus({
      volumeId: 2, isOnline: true, freeBytesLastKnown: 900, lastSeenUtc: '2026-08-18T09:00:00Z',
    });

    const [first, second] = store.volumes();
    expect(second.isOnline).toBe(true);
    expect(second.freeBytes).toBe(900);
    expect(second.lastSeenUtc).toBe('2026-08-18T09:00:00Z');
    expect(second.dataIsLive).toBe(true);
    expect(first.isOnline).toBe(false);
  });
});

describe('NotificationsStore fed by realtime', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('bumps the badge without fetching while the panel is closed', () => {
    const list = vi.fn(() => of(page([])));
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        {
          provide: NotificationsApi,
          useValue: { unreadCount: () => of({ unread: 0 }), list },
        },
      ],
    });
    const store = TestBed.inject(NotificationsStore);

    store.applyRaised();
    store.applyRaised();

    expect(store.unread()).toBe(2);
    expect(store.hasUnread()).toBe(true);
    expect(list).not.toHaveBeenCalled();
  });
});

describe('Projection invalidation fed by realtime', () => {
  afterEach(() => TestBed.resetTestingModule());

  const children: CatalogChildrenDto = {
    directories: [],
    files: page([]),
    volumeIsOnline: true,
    volumeLabel: 'SSD',
    volumeLetter: 'C:',
    currentDirectoryId: null,
    currentDirectoryPath: '',
  };

  const volume = { id: 3 } as VolumeDto;

  function setupCatalog() {
    const childrenApi = vi.fn(() => of(children));
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: CatalogApi, useValue: { children: childrenApi } },
      ],
    });
    return { store: TestBed.inject(CatalogStore), childrenApi };
  }

  it('reloads the open folder when the overlay moved on its volume', async () => {
    const { store, childrenApi } = setupCatalog();
    await store.selectVolume(volume);

    store.invalidate(3);

    expect(childrenApi).toHaveBeenCalledTimes(2);
  });

  it('reloads on a cross-volume change, which names no volume at all', async () => {
    const { store, childrenApi } = setupCatalog();
    await store.selectVolume(volume);

    store.invalidate(null);

    expect(childrenApi).toHaveBeenCalledTimes(2);
  });

  it('ignores a change on some other volume', async () => {
    const { store, childrenApi } = setupCatalog();
    await store.selectVolume(volume);

    store.invalidate(99);

    expect(childrenApi).toHaveBeenCalledTimes(1);
  });

  it('does nothing when no folder is open', () => {
    const { store, childrenApi } = setupCatalog();

    store.invalidate(null);

    expect(childrenApi).not.toHaveBeenCalled();
  });

  it('re-runs the current search page, and stays quiet with no results on screen', async () => {
    const search = vi.fn(() => of(page<SearchResultDto>([])));
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: SearchApi, useValue: { search } },
      ],
    });
    const store = TestBed.inject(SearchStore);

    store.invalidate();
    expect(search).not.toHaveBeenCalled();

    store.setQuery('raw');
    await store.search();
    store.invalidate();

    expect(search).toHaveBeenCalledTimes(2);
  });
});
