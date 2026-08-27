import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject, of } from 'rxjs';
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

  // ── the auto-selection race (roadmap A1, found by 12a and left there on purpose) ──

  /// A detail API whose answers the test releases by hand, one volume at a time.
  function deferredDetail() {
    const subjects: Record<number, Subject<VolumeDetailDto>> = {};
    const api = {
      detail: vi.fn((id: number) => (subjects[id] ??= new Subject<VolumeDetailDto>())),
    };
    const answer = (id: number, d: VolumeDetailDto) => { subjects[id].next(d); subjects[id].complete(); };
    const fail = (id: number, e: unknown) => subjects[id].error(e);
    return { api, answer, fail };
  }

  it('a slow earlier selection does not overwrite a newer one', async () => {
    // The shape of the defect: the screen auto-selects the first volume the moment the list
    // arrives, the user clicks another one, and whichever RESPONSE lands last wins. On a busy
    // machine that is the auto-selection, so the panel snaps back to a volume the user is no
    // longer looking at.
    const { api, answer } = deferredDetail();
    const store = configure(api as never);

    const first = store.select(1);   // the auto-selection
    const second = store.select(2);  // the user, a moment later

    answer(2, { ...detail, id: 2, label: 'Beta' });
    await second;
    expect(store.selected()?.id).toBe(2);

    // …and now the stale one lands.
    answer(1, { ...detail, id: 1, label: 'Alpha' });
    await first;

    expect(store.selected()?.id).toBe(2);
    expect(store.selected()?.label).toBe('Beta');
  });

  it('a stale response does not clear the spinner of the selection still running', async () => {
    const { api, answer } = deferredDetail();
    const store = configure(api as never);

    const first = store.select(1);
    const second = store.select(2);

    answer(1, { ...detail, id: 1 });
    await first;

    // Volume 2 is still loading: an answer nobody is waiting for must not say otherwise.
    expect(store.detailLoading()).toBe(true);
    expect(store.selected()).toBeNull();

    answer(2, { ...detail, id: 2 });
    await second;
    expect(store.detailLoading()).toBe(false);
    expect(store.selected()?.id).toBe(2);
  });

  it('a stale FAILURE does not report an error over a selection that is still running', async () => {
    const { api, answer, fail } = deferredDetail();
    const store = configure(api as never);

    const first = store.select(1);
    const second = store.select(2);

    fail(1, new Error('boom'));
    await first;

    expect(store.error()).toBeNull();

    answer(2, { ...detail, id: 2 });
    await second;
    expect(store.selected()?.id).toBe(2);
  });

  it('remembers what was ASKED for, not only what has arrived', async () => {
    // The screen guards its own clicks with this: reading the loaded detail instead would still
    // be the PREVIOUS volume while the new one is in flight, so a second click on the same row
    // fires a second request.
    const store = configure({ detail: vi.fn(() => of({ ...detail, id: 7 })) } as never);

    const pending = store.select(7);
    expect(store.selectedId()).toBe(7);
    await pending;
    expect(store.selectedId()).toBe(7);
  });

  it('clearing the selection forgets what was asked for too', async () => {
    const store = configure({ detail: vi.fn(() => of({ ...detail, id: 7 })) } as never);

    await store.select(7);
    store.clearSelection();

    expect(store.selectedId()).toBeNull();
    expect(store.selected()).toBeNull();
  });
});
