import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { afterEach, vi } from 'vitest';

import { DashboardApi } from '../../core/api/dashboard-api.service';
import { DashboardStatsDto } from '../../core/models/catalog.models';
import { DashboardStore } from './dashboard.store';

const stats: DashboardStatsDto = {
  totalFiles: 10,
  totalBytes: 1000,
  volumesOnline: 1,
  volumesTotal: 2,
  queuedJobs: 0,
  blockedJobs: 0,
  runningJobs: 0,
  pendingBytes: 0,
};

function configure(api: Partial<DashboardApi>) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), { provide: DashboardApi, useValue: api }],
  });
  return TestBed.inject(DashboardStore);
}

describe('DashboardStore', () => {
  it('loads stats into the signal', async () => {
    const store = configure({ getStats: () => of(stats) });
    await store.load();

    expect(store.stats()).toEqual(stats);
    expect(store.loading()).toBe(false);
    expect(store.error()).toBeNull();
  });

  it('captures the error message and clears loading', async () => {
    const store = configure({ getStats: () => throwError(() => new Error('Servizio non raggiungibile')) });
    await store.load();

    expect(store.stats()).toBeNull();
    expect(store.error()).toBe('Servizio non raggiungibile');
    expect(store.loading()).toBe(false);
  });
});

// C30 — the queue cards react to `JobStateChanged`. The push carries a job id and a state,
// never the bytes, so the only honest reaction is a re-read; the point of the coalescing is
// that a burst (an enqueue of many files, a job stepping through its states) is still one.
describe('DashboardStore queue refresh', () => {
  afterEach(() => vi.useRealTimers());

  it('collapses a burst of queue transitions into one re-read', async () => {
    const getStats = vi.fn(() => of(stats));
    const store = configure({ getStats });
    await store.load();

    vi.useFakeTimers();
    getStats.mockClear();
    for (let i = 0; i < 20; i++) store.scheduleRefresh();

    expect(getStats).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1000);
    expect(getStats).toHaveBeenCalledTimes(1);

    // ...and the window re-arms for the next burst.
    store.scheduleRefresh();
    vi.advanceTimersByTime(1000);
    expect(getStats).toHaveBeenCalledTimes(2);
  });

  it('does not poke a service whose first read never landed', async () => {
    const getStats = vi.fn(() => throwError(() => new Error('Servizio non raggiungibile')));
    const store = configure({ getStats });
    await store.load();

    vi.useFakeTimers();
    getStats.mockClear();
    store.scheduleRefresh();
    vi.advanceTimersByTime(5000);

    expect(getStats).not.toHaveBeenCalled();
  });
});
