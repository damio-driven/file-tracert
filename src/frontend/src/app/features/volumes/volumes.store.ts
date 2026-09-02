import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { firstValueFrom } from 'rxjs';

import { VolumesApi } from '../../core/api/volumes-api.service';
import { httpErrorMessage } from '../../core/http/http-error';
import { VolumeDetailDto, VolumeDto } from '../../core/models/catalog.models';
import { VolumeStatusChanged } from '../../core/realtime/realtime.models';

interface VolumesState {
  volumes: VolumeDto[];
  /**
   * The volume whose detail was last ASKED for, set the instant `select` is called. Distinct
   * from `selected`, which is the detail that has actually arrived: between the two lives the
   * request, and that gap is where the auto-selection race used to live.
   */
  selectedId: number | null;
  selected: VolumeDetailDto | null;
  loading: boolean;
  detailLoading: boolean;
  rescanningId: number | null;
  togglingId: number | null;
  error: string | null;
}

const initial: VolumesState = {
  volumes: [],
  selectedId: null,
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
        patchState(store, { error: httpErrorMessage(e), loading: false });
      }
    }

    return {
      loadList,

      /**
       * Loads one volume's detail. Two selections can be in flight at once — the screen
       * auto-selects the first catalogable volume the moment the list arrives, and the user can
       * click another one a moment later — and this used to `patchState` unconditionally, so the
       * panel showed whichever RESPONSE landed last rather than whichever the user asked for
       * last. On a busy machine that is the auto-selection, and the screen snapped back to a
       * volume nobody was looking at. (Found by the 12a end-to-end run and left there
       * deliberately; it is the defect that made one of those passes red.)
       *
       * `selectedId` is written SYNCHRONOUSLY, so it is already the newer id by the time an older
       * response returns: an answer nobody is waiting for is dropped whole — detail, spinner and
       * error alike, since a stale failure reported over a selection that is still running is the
       * same lie in a different colour.
       */
      async select(id: number): Promise<void> {
        patchState(store, { selectedId: id, detailLoading: true, error: null });
        try {
          const selected = await firstValueFrom(api.detail(id));
          if (store.selectedId() !== id) return;
          patchState(store, { selected, detailLoading: false });
        } catch (e) {
          if (store.selectedId() !== id) return;
          patchState(store, { error: httpErrorMessage(e), detailLoading: false });
        }
      },

      /**
       * `VolumeStatusChanged` push: a volume was mounted or unplugged. `dataIsLive` is the
       * server's derived flag, so it moves with `isOnline` here too — leaving it behind would
       * show a last-known figure as if it were live (§ honesty).
       */
      applyVolumeStatus(message: VolumeStatusChanged): void {
        const apply = <T extends { id: number; isOnline: boolean; freeBytes: number;
          lastSeenUtc: string; dataIsLive: boolean }>(v: T): T =>
          v.id === message.volumeId
            ? {
                ...v,
                isOnline: message.isOnline,
                freeBytes: message.freeBytesLastKnown,
                lastSeenUtc: message.lastSeenUtc,
                dataIsLive: message.isOnline,
              }
            : v;

        patchState(store, { volumes: store.volumes().map(apply) });

        const selected = store.selected();
        if (selected && selected.id === message.volumeId) {
          patchState(store, { selected: apply(selected) });
        }
      },

      clearSelection(): void {
        // All three. `selectedId` because leaving it behind would make the next click on that same
        // volume a no-op for the screen's own guard; `detailLoading` because this is the ONE caller
        // that moves `selectedId` without issuing a request — so a selection still in flight when
        // this runs has its answer dropped by the staleness guard, and nothing else would ever
        // clear the spinner. (Found by the final review of step 15b.)
        patchState(store, { selectedId: null, selected: null, detailLoading: false });
      },

      async rescan(id: number): Promise<void> {
        patchState(store, { rescanningId: id, error: null });
        try {
          await firstValueFrom(api.rescan(id));
        } catch (e) {
          patchState(store, { error: httpErrorMessage(e) });
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
          patchState(store, { error: httpErrorMessage(e) });
        } finally {
          patchState(store, { togglingId: null });
        }
        await loadList();
      },
    };
  }),
);
