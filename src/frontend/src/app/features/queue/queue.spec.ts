import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';

import { QueueApi } from '../../core/api/queue-api.service';
import { JobBlockReason, OperationJobDto, PagedResult } from '../../core/models/catalog.models';
import { Queue } from './queue';
import { QueueStore } from './queue.store';

const blockedOn = (reason: JobBlockReason, dependsOnJobId: number | null): OperationJobDto => ({
  id: 7,
  type: 'MoveFile',
  state: 'Blocked',
  blockReason: reason,
  sourceVolumeId: 1,
  sourceVolumeLabel: 'SSD',
  targetVolumeId: 1,
  targetVolumeLabel: 'SSD',
  sourcePath: 'Docs\\file.txt',
  targetPath: 'Archivio\\file.txt',
  isIntraVolume: true,
  totalBytes: 0,
  bytesProcessed: 0,
  requiredBytesTarget: 0,
  freedBytesSource: 0,
  estimateIsLive: true,
  sequenceOrder: 7,
  dependsOnJobId,
  errorMessage: null,
  createdUtc: '2026-01-01T00:00:00Z',
  startedUtc: null,
  completedUtc: null,
  feasibility: null,
});

interface Harness {
  dependencyOf(job: OperationJobDto): number | null;
  dependencyLead(reason: JobBlockReason): string;
}

function setup(): Harness {
  const empty: PagedResult<OperationJobDto> = { items: [], totalCount: 0, skip: 0, take: 50 };
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: QueueApi, useValue: { list: vi.fn(() => of(empty)) } },
      { provide: ActivatedRoute, useValue: { queryParamMap: of(new Map()) } },
    ],
  });
  const fixture = TestBed.createComponent(Queue);
  fixture.detectChanges();
  return fixture.componentInstance as unknown as Harness;
}

describe('Queue — blocked on another job', () => {
  it('offers the prerequisite as a link only when the block IS the dependency', () => {
    const cmp = setup();

    expect(cmp.dependencyOf(blockedOn('DependencyPending', 3))).toBe(3);
    expect(cmp.dependencyOf(blockedOn('DependencyCancelled', 3))).toBe(3);
    // Same column, different story: a space or offline block must keep its own label even
    // if the job happens to still carry a dependency it is no longer waiting on.
    expect(cmp.dependencyOf(blockedOn('InsufficientSpace', 3))).toBeNull();
    expect(cmp.dependencyOf(blockedOn('DependencyPending', null))).toBeNull();
  });

  it('reads the two dependency reasons apart', () => {
    const cmp = setup();

    expect(cmp.dependencyLead('DependencyPending')).toBe("In attesa dell'operazione");
    expect(cmp.dependencyLead('DependencyCancelled')).toBe('Dipendenza interrotta:');
  });
});

describe('Queue — no polling', () => {
  it('starts no timer of its own: the hub pushes the rows that change', () => {
    const interval = vi.spyOn(globalThis, 'setInterval');
    try {
      setup();
      // jsdom drives requestAnimationFrame off a ~16ms interval of its own, so the check is
      // "no polling cadence", not "no timer in the process".
      const polls = interval.mock.calls.filter(([, ms]) => Number(ms) >= 1_000);
      expect(polls).toEqual([]);
    } finally {
      interval.mockRestore();
    }
  });

  it('renders a row the store gained after init, with no reload of its own', async () => {
    const empty: PagedResult<OperationJobDto> = { items: [], totalCount: 0, skip: 0, take: 50 };
    const list = vi.fn(() => of(empty));
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: QueueApi, useValue: { list } },
        { provide: ActivatedRoute, useValue: { queryParamMap: of(new Map()) } },
      ],
    });
    const fixture = TestBed.createComponent(Queue);
    await fixture.whenStable();

    const store = TestBed.inject(QueueStore);
    await store.load();
    list.mockClear();

    store.applyStateChanged({
      jobId: 7, state: 'Completed', blockReason: 'None', errorMessage: null,
    });
    await fixture.whenStable();

    expect(list).not.toHaveBeenCalled();
  });
});
