import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { firstValueFrom } from 'rxjs';

import { SetupApi } from '../../core/api/setup-api.service';
import { VolumesApi } from '../../core/api/volumes-api.service';
import { httpErrorMessage } from '../../core/http/http-error';
import {
  FilterOverrideDto,
  FilterSettingsDto,
  FolderNodeDto,
  PagedResult,
  ReconcileResultDto,
  WatchedRootDto,
} from '../../core/models/catalog.models';

interface SetupState {
  volumeId: number | null;
  roots: WatchedRootDto[];
  filter: FilterSettingsDto | null;
  /** Per path: the folders fetched so far and how many the disk holds (step 17, paged). */
  browseCache: Record<string, PagedResult<FolderNodeDto>>;
  /** Paths whose next page is on its way. */
  browsingMore: Record<string, boolean>;
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
  browsingMore: {},
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
      patchState(store, { error: httpErrorMessage(e), loading: false, busy: false });
    }

    return {
      foldersAt: (path: string): FolderNodeDto[] => store.browseCache()[path]?.items ?? [],

      /** Folders of `path` the disk holds beyond the ones fetched (0 when the level is complete). */
      remainingFoldersAt: (path: string): number => {
        const page = store.browseCache()[path];
        return page ? Math.max(0, page.totalCount - page.items.length) : 0;
      },

      isBrowsingMore: (path: string): boolean => store.browsingMore()[path] === true,

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

      /**
       * Step 17: the next page of one level, APPENDED under the folders already in the tree.
       * Per path, so expanding one wide folder never touches the others.
       */
      async loadMoreFolders(path: string): Promise<void> {
        const current = store.browseCache()[path];
        if (!current || store.browsingMore()[path]) return;
        if (current.items.length >= current.totalCount) return;

        const volumeId = store.volumeId()!;
        patchState(store, (s) => ({ browsingMore: { ...s.browsingMore, [path]: true }, error: null }));
        try {
          const page = await firstValueFrom(api.browse(volumeId, path, current.items.length));
          // `init()` moved the store to another volume meanwhile: this page is not its.
          if (store.volumeId() !== volumeId) return;
          patchState(store, (s) => {
            const latest = s.browseCache[path] ?? current;
            const items = [...latest.items, ...page.items];
            return {
              browseCache: {
                ...s.browseCache,
                [path]: { items, totalCount: page.totalCount, skip: 0, take: items.length },
              },
            };
          });
        } catch (e) {
          fail(e);
        } finally {
          patchState(store, (s) => ({ browsingMore: { ...s.browsingMore, [path]: false } }));
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
          patchState(store, (s) => ({
            roots: s.roots.map((r) => (r.id === id ? res.root : r)),
            // Switching a folder off or on moves the index too (rows excluded, or included
            // again with no rescan), and switching one back on leaves behind whatever was
            // never indexed while it was off. Dropping this outcome asked the user to take
            // that on faith.
            lastReconcile: res.reconcile,
            busy: false,
          }));
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
