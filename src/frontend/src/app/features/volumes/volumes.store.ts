import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { firstValueFrom } from 'rxjs';

import { VolumesApi } from '../../core/api/volumes-api.service';
import { VolumeDetailDto, VolumeDto } from '../../core/models/catalog.models';
import { VolumeStatusChanged } from '../../core/realtime/realtime.models';

interface VolumesState {
  volumes: VolumeDto[];
  selected: VolumeDetailDto | null;
  loading: boolean;
  detailLoading: boolean;
  rescanningId: number | null;
  togglingId: number | null;
  error: string | null;
}

const initial: VolumesState = {
  volumes: [],
  selected: null,
  loading: false,
  detailLoading: false,
  rescanningId: null,
  togglingId: null,
  error: null,
};

/**
 * Volumes list + selected detail. The list is split into catalogable volumes (the
 * main view) and excluded ones (cloud/system, behind a "show all" toggle); the user
 * can re-enable a false positive. `rescan` triggers a server-side scan then refreshes
 * the list so counts/checkpoints catch up. `VolumeStatusChanged` pushes keep the online
 * flag and the free-space figure current without a reload (step 10c).
 */
export const VolumesStore = signalStore(
  { providedIn: 'root' },
  withState(initial),
  withComputed((store) => ({
    catalogable: computed(() => store.volumes().filter((v) => v.isCatalogable)),
    excluded: computed(() => store.volumes().filter((v) => !v.isCatalogable)),
  })),
  withMethods((store, api = inject(VolumesApi)) => {
    async function loadList(): Promise<void> {
      patchState(store, { loading: true, error: null });
      try {
        const volumes = await firstValueFrom(api.list());
        patchState(store, { volumes, loading: false });
      } catch (e) {
        patchState(store, { error: (e as Error).message, loading: false });
      }
    }

    return {
      loadList,

      async select(id: number): Promise<void> {
        patchState(store, { detailLoading: true, error: null });
        try {
          const selected = await firstValueFrom(api.detail(id));
          patchState(store, { selected, detailLoading: false });
        } catch (e) {
          patchState(store, { error: (e as Error).message, detailLoading: false });
        }
      },

      /**
       * `VolumeStatusChanged` push: a volume was mounted or unplugged. `dataIsLive` /
       * `isStale` are the server's derived flags, so they move with `isOnline` here too —
       * leaving them behind would show a last-known figure as if it were live (§ honesty).
       */
      applyVolumeStatus(message: VolumeStatusChanged): void {
        const apply = <T extends { id: number; isOnline: boolean; freeBytes: number;
          lastSeenUtc: string; dataIsLive: boolean; isStale: boolean }>(v: T): T =>
          v.id === message.volumeId
            ? {
                ...v,
                isOnline: message.isOnline,
                freeBytes: message.freeBytesLastKnown,
                lastSeenUtc: message.lastSeenUtc,
                dataIsLive: message.isOnline,
                isStale: !message.isOnline,
              }
            : v;

        patchState(store, { volumes: store.volumes().map(apply) });

        const selected = store.selected();
        if (selected && selected.id === message.volumeId) {
          patchState(store, { selected: apply(selected) });
        }
      },

      clearSelection(): void {
        patchState(store, { selected: null });
      },

      async rescan(id: number): Promise<void> {
        patchState(store, { rescanningId: id, error: null });
        try {
          await firstValueFrom(api.rescan(id));
        } catch (e) {
          patchState(store, { error: (e as Error).message });
        } finally {
          patchState(store, { rescanningId: null });
        }
        await loadList();
      },

      /** Re-enable (or exclude) a volume, then refresh so it moves between sections. */
      async setCatalogable(id: number, isCatalogable: boolean): Promise<void> {
        patchState(store, { togglingId: id, error: null });
        try {
          await firstValueFrom(api.setCatalogable(id, isCatalogable));
        } catch (e) {
          patchState(store, { error: (e as Error).message });
        } finally {
          patchState(store, { togglingId: null });
        }
        await loadList();
      },
    };
  }),
);
