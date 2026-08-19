import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { firstValueFrom } from 'rxjs';

import { QueueApi } from '../../core/api/queue-api.service';
import { httpErrorMessage } from '../../core/http/http-error';
import { OperationJobDto, PagedResult } from '../../core/models/catalog.models';
import { isActiveJobState, isQueuedJobState } from '../../core/models/job-state';
import { JobProgress, JobStateChanged } from '../../core/realtime/realtime.models';

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

/**
 * A state change about a job we don't have on screen means the page is out of date (a job
 * was enqueued elsewhere). We reload once, coalescing the burst an enqueue of many files
 * produces — this is a reaction to a push, not a timer that runs on its own.
 */
const UNKNOWN_JOB_RELOAD_MS = 400;

export const QueueStore = signalStore(
  { providedIn: 'root' },
  withState(initial),
  withComputed((store) => ({
    jobs: computed(() => store.result()?.items ?? []),
    totalCount: computed(() => store.result()?.totalCount ?? 0),
    hasJobs: computed(() => (store.result()?.totalCount ?? 0) > 0),
    hasActiveJobs: computed(() =>
      (store.result()?.items ?? []).some(j => isActiveJobState(j.state))
    ),
    activeCount: computed(() =>
      (store.result()?.items ?? []).filter(j => isActiveJobState(j.state)).length
    ),
    blockedCount: computed(() =>
      (store.result()?.items ?? []).filter(j => j.state === 'Blocked').length
    ),
    pendingCount: computed(() =>
      (store.result()?.items ?? []).filter(
        j => isQueuedJobState(j.state)
      ).length
    ),
  })),
  withMethods((store, api = inject(QueueApi)) => {
    async function doLoad(skip: number, take: number): Promise<void> {
      patchState(store, { loading: true, error: null });
      try {
        const result = await firstValueFrom(api.list(skip, take));
        patchState(store, { result, loading: false, skip, take });
      } catch (e) {
        patchState(store, { error: httpErrorMessage(e), loading: false });
      }
    }

    /** Immutable single-row patch. Returns false when the job is not on the current page. */
    function patchRow(
      jobId: number,
      update: (job: OperationJobDto) => OperationJobDto,
    ): boolean {
      const result = store.result();
      const index = result?.items.findIndex(j => j.id === jobId) ?? -1;
      if (!result || index < 0) {
        return false;
      }
      const items = [...result.items];
      items[index] = update(items[index]);
      patchState(store, { result: { ...result, items } });
      return true;
    }

    let reloadHandle: ReturnType<typeof setTimeout> | null = null;

    function scheduleReload(): void {
      if (reloadHandle !== null) {
        return;
      }
      reloadHandle = setTimeout(() => {
        reloadHandle = null;
        void doLoad(store.skip(), store.take());
      }, UNKNOWN_JOB_RELOAD_MS);
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
          patchState(store, { error: httpErrorMessage(e) });
        } finally {
          patchState(store, { cancellingIds: store.cancellingIds().filter(x => x !== id) });
        }
      },
      /**
       * `JobProgress` push: patch the one row's byte counter. Deliberately NOT a reload —
       * the engine emits this once a second per running job.
       */
      applyProgress(message: JobProgress): void {
        patchRow(message.jobId, (job) => ({
          ...job,
          bytesProcessed: message.bytesProcessed,
          totalBytes: message.totalBytes,
        }));
      },

      /**
       * `JobStateChanged` push: patch state/blockReason/error on the row. A job we have
       * never seen means the list is stale, so reload it once (coalesced); a message that
       * arrives before the first load is simply dropped, there is nothing to be stale about.
       */
      applyStateChanged(message: JobStateChanged): void {
        const patched = patchRow(message.jobId, (job) => ({
          ...job,
          state: message.state,
          blockReason: message.blockReason,
          errorMessage: message.errorMessage,
        }));
        if (!patched && store.result() !== null) {
          scheduleReload();
        }
      },

      /** Riprova: puts a Blocked/Failed job back in queue and reloads. */
      async retry(id: number): Promise<void> {
        patchState(store, { retryingIds: [...store.retryingIds(), id] });
        try {
          await firstValueFrom(api.retry(id));
          await doLoad(store.skip(), store.take());
        } catch (e) {
          patchState(store, { error: httpErrorMessage(e) });
        } finally {
          patchState(store, { retryingIds: store.retryingIds().filter(x => x !== id) });
        }
      },
    };
  }),
);
