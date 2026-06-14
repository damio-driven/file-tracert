import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { vi } from 'vitest';

import { SetupApi } from '../../core/api/setup-api.service';
import { VolumesApi } from '../../core/api/volumes-api.service';
import { FolderNodeDto, WatchedRootDto } from '../../core/models/catalog.models';
import { SetupStore } from './setup.store';

const fotoRoot: WatchedRootDto = { id: 7, relativePath: 'Foto', isActive: true, effectiveFilter: 'Immagini' };
const topFolders: FolderNodeDto[] = [{ name: 'Foto', relativePath: 'Foto', hasChildren: true }];

function configure(api: Partial<SetupApi>, volumesApi: Partial<VolumesApi> = {}) {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: SetupApi, useValue: api },
      { provide: VolumesApi, useValue: volumesApi },
    ],
  });
  return TestBed.inject(SetupStore);
}

describe('SetupStore', () => {
  it('caches folders per path on lazy browse', async () => {
    const browse = vi.fn(() => of(topFolders));
    const store = configure({ browse });
    store.init(1);

    await store.loadFolders('');
    await store.loadFolders('');

    expect(browse).toHaveBeenCalledTimes(1);
    expect(store.foldersAt('')).toEqual(topFolders);
  });

  it('adds a root and keeps it in state', async () => {
    const store = configure({ browse: () => of(topFolders), createRoot: () => of(fotoRoot) });
    store.init(1);

    await store.addRoot('Foto');

    expect(store.roots()).toContainEqual(fotoRoot);
  });

  it('surfaces the reconcile flag when saving the filter', async () => {
    const store = configure({
      getFilter: () => of({ allowedExtensions: ['jpg'], excludedPaths: [] }),
      putFilter: () => of({ includedCount: 1, excludedCount: 0, needsScan: true }),
    });
    store.init(1);

    await store.saveFilter({ allowedExtensions: ['jpg', 'png'], excludedPaths: [] });

    expect(store.lastReconcile()?.needsScan).toBe(true);
  });
});
