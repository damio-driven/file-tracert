import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { vi } from 'vitest';

import { ScansApi } from '../../core/api/scans-api.service';
import { ScanStatusDto } from '../../core/models/catalog.models';
import { ScanStatusStore } from './scan-status.store';

const scan: ScanStatusDto = {
  volumeId: 7,
  label: 'Data',
  phase: 'Writing',
  itemsSeen: 142_000,
  itemsWritten: 80_000,
  currentRoot: 'Photos',
  startedUtc: '2026-06-15T10:00:00Z',
  updatedUtc: '2026-06-15T10:00:05Z',
};

function configure(status: () => ReturnType<ScansApi['status']>) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), { provide: ScansApi, useValue: { status } }],
  });
  return TestBed.inject(ScanStatusStore);
}

describe('ScanStatusStore', () => {
  it('starts empty and not scanning', () => {
    const store = configure(() => of([]));
    expect(store.isScanning()).toBe(false);
    expect(store.activeCount()).toBe(0);
  });

  it('refresh populates active scans and exposes a per-volume lookup', async () => {
    const store = configure(() => of([scan]));
    await store.refresh();

    expect(store.isScanning()).toBe(true);
    expect(store.activeCount()).toBe(1);
    expect(store.byVolume().get(7)?.phase).toBe('Writing');
    store.stop();
  });

  it('swallows a failed poll without throwing', async () => {
    const store = configure(() => {
      throw new Error('network down');
    });

    await expect(store.refresh()).resolves.toBeUndefined();
    expect(store.isScanning()).toBe(false);
  });

  it('start is idempotent and polls at least once', async () => {
    const status = vi.fn(() => of([scan]));
    const store = configure(status);

    store.start();
    store.start();
    await Promise.resolve();

    expect(status).toHaveBeenCalled();
    store.stop();
  });
});
