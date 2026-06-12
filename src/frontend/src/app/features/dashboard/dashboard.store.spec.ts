import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

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
