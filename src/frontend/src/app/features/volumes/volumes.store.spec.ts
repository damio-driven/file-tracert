import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { vi } from 'vitest';

import { VolumesApi } from '../../core/api/volumes-api.service';
import { VolumeDetailDto, VolumeDto } from '../../core/models/catalog.models';
import { VolumesStore } from './volumes.store';

const volume: VolumeDto = {
  id: 1,
  volumeGuid: '\\\\?\\Volume{x}\\',
  label: 'Alpha',
  currentLetter: 'D:',
  fileSystem: 'NTFS',
  isRemovable: false,
  isOnline: true,
  lastSeenUtc: '2026-06-12T09:00:00Z',
  capacityBytes: 1000,
  freeBytes: 300,
  fileCount: 5,
  lastFullScanUtc: null,
  dataIsLive: true,
  isStale: false,
  kind: 'Fixed',
  isCatalogable: true,
};

const detail: VolumeDetailDto = {
  ...volume,
  serialNumber: 'ABCD',
  physicalDiskId: null,
  lastUsn: 42,
  scanEngine: 'UsnJournal',
  watchedRoots: [],
  directoryCount: 2,
  indexedBytes: 700,
};

function configure(api: Partial<VolumesApi>) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), { provide: VolumesApi, useValue: api }],
  });
  return TestBed.inject(VolumesStore);
}

describe('VolumesStore', () => {
  it('loads the list', async () => {
    const store = configure({ list: () => of([volume]) });
    await store.loadList();

    expect(store.volumes()).toHaveLength(1);
    expect(store.volumes()[0].label).toBe('Alpha');
  });

  it('loads a selected detail', async () => {
    const store = configure({ detail: () => of(detail) });
    await store.select(1);

    expect(store.selected()?.serialNumber).toBe('ABCD');
    expect(store.detailLoading()).toBe(false);
  });

  it('rescan calls the API then refreshes the list', async () => {
    const rescan = vi.fn(() => of(undefined));
    const list = vi.fn(() => of([volume]));
    const store = configure({ rescan, list });

    await store.rescan(1);

    expect(rescan).toHaveBeenCalledWith(1);
    expect(list).toHaveBeenCalled();
    expect(store.rescanningId()).toBeNull();
  });

  it('splits the list into catalogable and excluded volumes', async () => {
    const cloud: VolumeDto = { ...volume, id: 2, label: 'GoogleDrive', isCatalogable: false, kind: 'Cloud' };
    const store = configure({ list: () => of([volume, cloud]) });
    await store.loadList();

    expect(store.catalogable().map((v) => v.id)).toEqual([1]);
    expect(store.excluded().map((v) => v.id)).toEqual([2]);
  });

  it('setCatalogable calls the API then refreshes the list', async () => {
    const setCatalogable = vi.fn(() => of(undefined));
    const list = vi.fn(() => of([volume]));
    const store = configure({ setCatalogable, list });

    await store.setCatalogable(2, true);

    expect(setCatalogable).toHaveBeenCalledWith(2, true);
    expect(list).toHaveBeenCalled();
    expect(store.togglingId()).toBeNull();
  });
});
