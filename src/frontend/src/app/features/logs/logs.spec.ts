import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';

import { LogsApi } from '../../core/api/logs-api.service';
import { LogEntryDto } from '../../core/models/catalog.models';
import { LogsStore } from './logs.store';
import { Logs } from './logs';

const entries: LogEntryDto[] = [
  {
    id: 1,
    timestampUtc: '2026-06-01T10:00:00Z',
    level: 'Error',
    category: 'FileTracert.Host',
    message: 'scan failed',
    exception: 'System.Exception: boom\n   at X',
    eventId: null,
    scope: null,
  },
];

function api(overrides: Partial<LogsApi> = {}) {
  return {
    getLevel: () => of({ level: 'Information' }),
    getLogs: () => of({ items: entries, totalCount: 1, skip: 0, take: 50 }),
    setLevel: vi.fn(() => of({ level: 'Warning' })),
    ...overrides,
  };
}

describe('Logs screen', () => {
  it('renders the table with a level pill and category', async () => {
    TestBed.configureTestingModule({
      imports: [Logs],
      providers: [provideZonelessChangeDetection(), { provide: LogsApi, useValue: api() }],
    });

    const fixture = TestBed.createComponent(Logs);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('.ft-h1')?.textContent).toContain('Log');
    expect(el.textContent).toContain('scan failed');
    expect(el.textContent).toContain('FileTracert.Host');
    expect(el.querySelector('.ft-pill')?.textContent).toContain('Error');
  });

  it('expands the exception when a row with one is clicked', async () => {
    TestBed.configureTestingModule({
      imports: [Logs],
      providers: [provideZonelessChangeDetection(), { provide: LogsApi, useValue: api() }],
    });

    const fixture = TestBed.createComponent(Logs);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('.logs-exception')).toBeNull();
    el.querySelector<HTMLElement>('.logs-row.has-exception')!.click();
    await fixture.whenStable();

    expect(el.querySelector('.logs-exception')?.textContent).toContain('boom');
  });

  it('reflects persisted filters in the controls on re-entry', async () => {
    TestBed.configureTestingModule({
      imports: [Logs],
      providers: [provideZonelessChangeDetection(), { provide: LogsApi, useValue: api() }],
    });

    // Simulate filters set before leaving the screen (the store is a root singleton).
    await TestBed.inject(LogsStore).applyFilters({ level: 'Error', category: 'FileTracert', search: 'boom' });

    const fixture = TestBed.createComponent(Logs);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector<HTMLSelectElement>('.logs-filters .logs-select')!.value).toBe('Error');
    expect(el.querySelector<HTMLInputElement>('input.logs-input.mono')!.value).toBe('FileTracert');
    expect(el.querySelector<HTMLInputElement>('input[type="search"]')!.value).toBe('boom');
  });

  it('changes the runtime logging level via the control', async () => {
    const setLevel = vi.fn(() => of({ level: 'Warning' }));
    TestBed.configureTestingModule({
      imports: [Logs],
      providers: [provideZonelessChangeDetection(), { provide: LogsApi, useValue: api({ setLevel }) }],
    });

    const fixture = TestBed.createComponent(Logs);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    const levelSelect = el.querySelector<HTMLSelectElement>('.logs-level .logs-select')!;
    levelSelect.value = 'Warning';
    levelSelect.dispatchEvent(new Event('change'));
    await fixture.whenStable();

    expect(setLevel).toHaveBeenCalledWith('Warning');
  });
});
