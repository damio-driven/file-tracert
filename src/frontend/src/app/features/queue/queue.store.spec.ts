import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { QueueApi } from '../../core/api/queue-api.service';
import { OperationJobDto, PagedResult } from '../../core/models/catalog.models';
import { QueueStore } from './queue.store';

const makeJob = (id: number, state: OperationJobDto['state']): OperationJobDto => ({
  id,
  type: 'MoveFile',
  state,
  blockReason: 'None',
  sourceVolumeId: 1,
  sourceVolumeLabel: 'SSD',
  targetVolumeId: 2,
  targetVolumeLabel: 'USB',
  sourcePath: 'Docs\\file.txt',
  targetPath: 'Backup\\file.txt',
  isIntraVolume: false,
  totalBytes: 1024,
  bytesProcessed: 0,
  requiredBytesTarget: 1024,
  freedBytesSource: 0,
  estimateIsLive: true,
  sequenceOrder: id,
  dependsOnJobId: null,
  errorMessage: null,
  createdUtc: '2026-01-01T00:00:00Z',
  startedUtc: null,
  completedUtc: null,
  feasibility: null,
});

const pagedResult = (jobs: OperationJobDto[]): PagedResult<OperationJobDto> => ({
  items: jobs,
  totalCount: jobs.length,
  skip: 0,
  take: 50,
});

function setup(apiMock: Partial<QueueApi> = {}) {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      {
        provide: QueueApi,
        useValue: {
          list: vi.fn(() => of(pagedResult([]))),
          cancel: vi.fn(() => of(undefined)),
          retry: vi.fn(() => of(makeJob(1, 'Pending'))),
          enqueue: vi.fn(() => of(makeJob(1, 'Pending'))),
          preview: vi.fn(() => of({})),
          ...apiMock,
        },
      },
    ],
  });
  return TestBed.inject(QueueStore);
}

describe('QueueStore', () => {
  it('initialises with empty state', () => {
    const store = setup();
    expect(store.jobs()).toEqual([]);
    expect(store.loading()).toBe(false);
    expect(store.error()).toBeNull();
    expect(store.totalCount()).toBe(0);
    expect(store.hasJobs()).toBe(false);
  });

  it('load populates jobs from API', async () => {
    const jobs = [makeJob(1, 'Pending'), makeJob(2, 'Copying')];
    const store = setup({ list: vi.fn(() => of(pagedResult(jobs))) });

    await store.load();

    expect(store.jobs()).toHaveLength(2);
    expect(store.totalCount()).toBe(2);
    expect(store.hasJobs()).toBe(true);
  });

  it('hasActiveJobs true when Copying job exists', async () => {
    const store = setup({ list: vi.fn(() => of(pagedResult([makeJob(1, 'Copying')]))) });
    await store.load();
    expect(store.hasActiveJobs()).toBe(true);
  });

  it('hasActiveJobs true when Verifying job exists', async () => {
    const store = setup({ list: vi.fn(() => of(pagedResult([makeJob(1, 'Verifying')]))) });
    await store.load();
    expect(store.hasActiveJobs()).toBe(true);
  });

  it('hasActiveJobs false when only terminal jobs', async () => {
    const store = setup({
      list: vi.fn(() => of(pagedResult([makeJob(1, 'Completed'), makeJob(2, 'Cancelled')]))),
    });
    await store.load();
    expect(store.hasActiveJobs()).toBe(false);
  });

  it('activeCount counts Copying + Verifying + DeletingSource', async () => {
    const jobs = [
      makeJob(1, 'Copying'),
      makeJob(2, 'Verifying'),
      makeJob(3, 'DeletingSource'),
      makeJob(4, 'Pending'),
    ];
    const store = setup({ list: vi.fn(() => of(pagedResult(jobs))) });
    await store.load();
    expect(store.activeCount()).toBe(3);
  });

  it('blockedCount counts Blocked jobs', async () => {
    const jobs = [makeJob(1, 'Blocked'), makeJob(2, 'Blocked'), makeJob(3, 'Pending')];
    const store = setup({ list: vi.fn(() => of(pagedResult(jobs))) });
    await store.load();
    expect(store.blockedCount()).toBe(2);
  });

  it('pendingCount counts Pending and SpaceReserved', async () => {
    const jobs = [makeJob(1, 'Pending'), makeJob(2, 'SpaceReserved'), makeJob(3, 'Copying')];
    const store = setup({ list: vi.fn(() => of(pagedResult(jobs))) });
    await store.load();
    expect(store.pendingCount()).toBe(2);
  });

  it('cancel calls API and refreshes list', async () => {
    const cancelSpy = vi.fn(() => of(undefined));
    const listSpy = vi.fn()
      .mockReturnValueOnce(of(pagedResult([makeJob(1, 'Pending')])))
      .mockReturnValueOnce(of(pagedResult([])));

    const store = setup({ list: listSpy, cancel: cancelSpy });
    await store.load();
    expect(store.jobs()).toHaveLength(1);

    await store.cancel(1);

    expect(cancelSpy).toHaveBeenCalledWith(1);
    expect(store.jobs()).toHaveLength(0);
  });

  it('cancel removes id from cancellingIds after completion', async () => {
    const store = setup();
    await store.cancel(99);
    expect(store.cancellingIds()).not.toContain(99);
  });

  // FEATURE Riprova — manual retry of Blocked/Failed jobs.
  it('retry calls API and refreshes list', async () => {
    const retrySpy = vi.fn(() => of(makeJob(1, 'Pending')));
    const listSpy = vi.fn()
      .mockReturnValueOnce(of(pagedResult([makeJob(1, 'Failed')])))
      .mockReturnValueOnce(of(pagedResult([makeJob(1, 'Pending')])));

    const store = setup({ list: listSpy, retry: retrySpy });
    await store.load();
    expect(store.jobs()[0].state).toBe('Failed');

    await store.retry(1);

    expect(retrySpy).toHaveBeenCalledWith(1);
    expect(store.jobs()[0].state).toBe('Pending');
  });

  it('retry removes id from retryingIds after completion', async () => {
    const store = setup();
    await store.retry(99);
    expect(store.retryingIds()).not.toContain(99);
  });

  it('error state set on retry failure', async () => {
    const store = setup({
      retry: vi.fn(() => throwError(() => new Error('Cannot retry'))),
    });
    await store.retry(1);
    expect(store.error()).toBe('Cannot retry');
  });

  it('error state set on load failure', async () => {
    const store = setup({
      list: vi.fn(() => throwError(() => new Error('Network error'))),
    });
    await store.load();
    expect(store.error()).toBe('Network error');
    expect(store.loading()).toBe(false);
  });

  it('error state set on cancel failure', async () => {
    const store = setup({
      cancel: vi.fn(() => throwError(() => new Error('Cannot cancel'))),
    });
    await store.cancel(1);
    expect(store.error()).toBe('Cannot cancel');
  });

  it('refresh reloads using current skip/take', async () => {
    const listSpy = vi.fn(() => of(pagedResult([makeJob(1, 'Pending')])));
    const store = setup({ list: listSpy });
    await store.load(0, 25);
    await store.refresh();

    // refresh should use skip=0, take=25 (same as last load)
    expect(listSpy).toHaveBeenNthCalledWith(2, 0, 25);
  });
});
