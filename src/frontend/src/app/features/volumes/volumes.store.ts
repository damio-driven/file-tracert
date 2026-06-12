import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { firstValueFrom } from 'rxjs';

import { VolumesApi } from '../../core/api/volumes-api.service';
import { VolumeDetailDto, VolumeDto } from '../../core/models/catalog.models';

interface VolumesState {
  volumes: VolumeDto[];
  selected: VolumeDetailDto | null;
  loading: boolean;
  detailLoading: boolean;
  rescanningId: number | null;
  error: string | null;
}

const initial: VolumesState = {
  volumes: [],
  selected: null,
  loading: false,
  detailLoading: false,
  rescanningId: null,
  error: null,
};

/**
 * Volumes list + selected detail. `rescan` triggers a server-side scan then
 * refreshes the list so counts/checkpoints catch up. No real-time yet (step 10).
 */
export const VolumesStore = signalStore(
  { providedIn: 'root' },
  withState(initial),
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
    };
  }),
);
