import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { firstValueFrom } from 'rxjs';

import { SetupApi } from '../../core/api/setup-api.service';
import { VolumesApi } from '../../core/api/volumes-api.service';
import {
  FilterOverrideDto,
  FilterSettingsDto,
  FolderNodeDto,
  ReconcileResultDto,
  WatchedRootDto,
} from '../../core/models/catalog.models';

interface SetupState {
  volumeId: number | null;
  roots: WatchedRootDto[];
  filter: FilterSettingsDto | null;
  browseCache: Record<string, FolderNodeDto[]>;
  loading: boolean;
  busy: boolean;
  error: string | null;
  lastReconcile: ReconcileResultDto | null;
}

const initial: SetupState = {
  volumeId: null,
  roots: [],
  filter: null,
  browseCache: {},
  loading: false,
  busy: false,
  error: null,
  lastReconcile: null,
};

/**
 * Setup state for one volume: lazily browsed real-filesystem folders (cached per
 * path), the configured watched roots, and the global default filter. Filter
 * saves surface the reconcile outcome so the screen can say "serve scansione".
 */
export const SetupStore = signalStore(
  { providedIn: 'root' },
  withState(initial),
  withMethods((store, api = inject(SetupApi), volumesApi = inject(VolumesApi)) => {
    function fail(e: unknown): void {
      patchState(store, { error: (e as Error).message, loading: false, busy: false });
    }

    return {
      foldersAt: (path: string): FolderNodeDto[] => store.browseCache()[path] ?? [],

      init(volumeId: number): void {
        patchState(store, { ...initial, volumeId });
      },

      async loadFolders(path: string): Promise<void> {
        if (store.browseCache()[path]) {
          return;
        }
        patchState(store, { loading: true, error: null });
        try {
          const folders = await firstValueFrom(api.browse(store.volumeId()!, path));
          patchState(store, (s) => ({ browseCache: { ...s.browseCache, [path]: folders }, loading: false }));
        } catch (e) {
          fail(e);
        }
      },

      async addRoot(path: string): Promise<void> {
        patchState(store, { busy: true, error: null });
        try {
          const root = await firstValueFrom(
            api.createRoot(store.volumeId()!, { relativePath: path, filterOverride: null }),
          );
          patchState(store, (s) => ({ roots: [...s.roots, root], busy: false }));
        } catch (e) {
          fail(e);
        }
      },

      async removeRoot(id: number): Promise<void> {
        patchState(store, { busy: true, error: null });
        try {
          await firstValueFrom(api.deleteRoot(id));
          patchState(store, (s) => ({ roots: s.roots.filter((r) => r.id !== id), busy: false }));
        } catch (e) {
          fail(e);
        }
      },

      async toggleRoot(id: number, isActive: boolean): Promise<void> {
        patchState(store, { busy: true, error: null });
        try {
          const res = await firstValueFrom(api.updateRoot(id, { isActive, filterOverride: null }));
          patchState(store, (s) => ({ roots: s.roots.map((r) => (r.id === id ? res.root : r)), busy: false }));
        } catch (e) {
          fail(e);
        }
      },

      async setRootOverride(id: number, override: FilterOverrideDto): Promise<void> {
        patchState(store, { busy: true, error: null });
        try {
          const res = await firstValueFrom(api.updateRoot(id, { isActive: null, filterOverride: override }));
          patchState(store, (s) => ({
            roots: s.roots.map((r) => (r.id === id ? res.root : r)),
            lastReconcile: res.reconcile,
            busy: false,
          }));
        } catch (e) {
          fail(e);
        }
      },

      async loadRoots(): Promise<void> {
        patchState(store, { loading: true, error: null });
        try {
          const detail = await firstValueFrom(volumesApi.detail(store.volumeId()!));
          patchState(store, { roots: detail.watchedRoots, loading: false });
        } catch (e) {
          fail(e);
        }
      },

      async loadFilter(): Promise<void> {
        try {
          const filter = await firstValueFrom(api.getFilter());
          patchState(store, { filter });
        } catch (e) {
          fail(e);
        }
      },

      async saveFilter(dto: FilterSettingsDto): Promise<void> {
        patchState(store, { busy: true, error: null });
        try {
          const reconcile = await firstValueFrom(api.putFilter(dto));
          patchState(store, { filter: dto, lastReconcile: reconcile, busy: false });
        } catch (e) {
          fail(e);
        }
      },

      async triggerScan(): Promise<void> {
        patchState(store, { busy: true, error: null });
        try {
          await firstValueFrom(volumesApi.rescan(store.volumeId()!));
          patchState(store, { busy: false });
        } catch (e) {
          fail(e);
        }
      },
    };
  }),
);
