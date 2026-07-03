import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { firstValueFrom } from 'rxjs';

import { QueueApi } from '../../core/api/queue-api.service';
import { OperationJobDto, PagedResult } from '../../core/models/catalog.models';

interface QueueState {
  result: PagedResult<OperationJobDto> | null;
  loading: boolean;
  error: string | null;
  skip: number;
  take: number;
  cancellingIds: number[];
  retryingIds: number[];
}

const initial: QueueState = {
  result: null,
  loading: false,
  error: null,
  skip: 0,
  take: 50,
  cancellingIds: [],
  retryingIds: [],
};

const ACTIVE_STATES = new Set(['Copying', 'Verifying', 'DeletingSource']);
const TERMINAL_STATES = new Set(['Completed', 'Failed', 'Cancelled']);

export const QueueStore = signalStore(
  { providedIn: 'root' },
  withState(initial),
  withComputed((store) => ({
    jobs: computed(() => store.result()?.items ?? []),
    totalCount: computed(() => store.result()?.totalCount ?? 0),
    hasJobs: computed(() => (store.result()?.totalCount ?? 0) > 0),
    hasActiveJobs: computed(() =>
      (store.result()?.items ?? []).some(j => ACTIVE_STATES.has(j.state))
    ),
    activeCount: computed(() =>
      (store.result()?.items ?? []).filter(j => ACTIVE_STATES.has(j.state)).length
    ),
    blockedCount: computed(() =>
      (store.result()?.items ?? []).filter(j => j.state === 'Blocked').length
    ),
    pendingCount: computed(() =>
      (store.result()?.items ?? []).filter(
        j => j.state === 'Pending' || j.state === 'SpaceReserved'
      ).length
    ),
    completedCount: computed(() =>
      (store.result()?.items ?? []).filter(j => TERMINAL_STATES.has(j.state)).length
    ),
  })),
  withMethods((store, api = inject(QueueApi)) => {
    async function doLoad(skip: number, take: number): Promise<void> {
      patchState(store, { loading: true, error: null });
      try {
        const result = await firstValueFrom(api.list(skip, take));
        patchState(store, { result, loading: false, skip, take });
      } catch (e) {
        patchState(store, { error: (e as Error).message, loading: false });
      }
    }

    return {
      async load(skip = 0, take = 50): Promise<void> {
        await doLoad(skip, take);
      },
      async refresh(): Promise<void> {
        await doLoad(store.skip(), store.take());
      },
      async cancel(id: number): Promise<void> {
        patchState(store, { cancellingIds: [...store.cancellingIds(), id] });
        try {
          await firstValueFrom(api.cancel(id));
          await doLoad(store.skip(), store.take());
        } catch (e) {
          patchState(store, { error: (e as Error).message });
        } finally {
          patchState(store, { cancellingIds: store.cancellingIds().filter(x => x !== id) });
        }
      },
      /** Riprova: puts a Blocked/Failed job back in queue and reloads. */
      async retry(id: number): Promise<void> {
        patchState(store, { retryingIds: [...store.retryingIds(), id] });
        try {
          await firstValueFrom(api.retry(id));
          await doLoad(store.skip(), store.take());
        } catch (e) {
          patchState(store, { error: (e as Error).message });
        } finally {
          patchState(store, { retryingIds: store.retryingIds().filter(x => x !== id) });
        }
      },
    };
  }),
);
