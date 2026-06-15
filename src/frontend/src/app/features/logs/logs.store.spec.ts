import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';

import { LogsApi, LogQueryParams } from '../../core/api/logs-api.service';
import { LogEntryDto, PagedResult } from '../../core/models/catalog.models';
import { LogsStore } from './logs.store';

function entry(id: number): LogEntryDto {
  return {
    id,
    timestampUtc: '2026-06-01T10:00:00Z',
    level: 'Information',
    category: 'Test',
    message: `m${id}`,
    exception: null,
    eventId: null,
    scope: null,
  };
}

function page(items: LogEntryDto[], total: number, skip = 0): PagedResult<LogEntryDto> {
  return { items, totalCount: total, skip, take: 50 };
}

function configure(api: Partial<LogsApi>) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), { provide: LogsApi, useValue: api }],
  });
  return TestBed.inject(LogsStore);
}

describe('LogsStore', () => {
  it('init loads the runtime level and the first page', async () => {
    const store = configure({
      getLevel: () => of({ level: 'Debug' }),
      getLogs: () => of(page([entry(1), entry(2)], 2)),
    });

    await store.init();

    expect(store.currentLevel()).toBe('Debug');
    expect(store.entries()).toHaveLength(2);
    expect(store.totalCount()).toBe(2);
  });

  it('applyFilters passes filters and resets to the first page', async () => {
    const getLogs = vi.fn((q: LogQueryParams) => of(page([entry(1)], 1)));
    const store = configure({ getLevel: () => of({ level: 'Information' }), getLogs });

    await store.init();
    await store.applyFilters({ level: 'Error', category: 'Scan', search: 'boom' });

    expect(store.skip()).toBe(0);
    const lastCall = getLogs.mock.calls.at(-1)![0];
    expect(lastCall).toMatchObject({ level: 'Error', category: 'Scan', search: 'boom' });
  });

  it('paginates forward and back', async () => {
    const store = configure({
      getLevel: () => of({ level: 'Information' }),
      getLogs: () => of(page([entry(1)], 120)),
    });

    await store.init();
    expect(store.hasNext()).toBe(true);

    await store.nextPage();
    expect(store.skip()).toBe(50);
    expect(store.page()).toBe(2);

    await store.prevPage();
    expect(store.skip()).toBe(0);
  });

  it('changeLevel applies the level returned by the server', async () => {
    const setLevel = vi.fn(() => of({ level: 'Warning' }));
    const store = configure({
      getLevel: () => of({ level: 'Information' }),
      getLogs: () => of(page([], 0)),
      setLevel,
    });

    await store.init();
    await store.changeLevel('Warning');

    expect(setLevel).toHaveBeenCalledWith('Warning');
    expect(store.currentLevel()).toBe('Warning');
  });
});
