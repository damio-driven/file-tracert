import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { firstValueFrom } from 'rxjs';

import { ScansApi } from '../../core/api/scans-api.service';
import { ScanStatusDto } from '../../core/models/catalog.models';

interface ScanStatusState {
  active: ScanStatusDto[];
}

const initial: ScanStatusState = { active: [] };

/** Fast cadence while a scan is in flight; light heartbeat otherwise. */
const ACTIVE_POLL_MS = 1_500;
const IDLE_POLL_MS = 10_000;

/**
 * Polls the in-memory scan tracker. The cadence adapts: ~1.5s while at least one
 * scan is active (live progress bars), a light ~10s heartbeat when idle to notice
 * a newly started scan. At step 10 the SignalR client pushes the same shape into
 * this store and the polling is removed; consumers stay unchanged.
 */
export const ScanStatusStore = signalStore(
  { providedIn: 'root' },
  withState(initial),
  withComputed((store) => ({
    isScanning: computed(() => store.active().length > 0),
    activeCount: computed(() => store.active().length),
    byVolume: computed(() => new Map(store.active().map((s) => [s.volumeId, s]))),
  })),
  withMethods((store, api = inject(ScansApi)) => {
    let handle: ReturnType<typeof setTimeout> | null = null;
    let running = false;

    async function refresh(): Promise<void> {
      try {
        const active = await firstValueFrom(api.status());
        patchState(store, { active });
      } catch {
        // Progress is best-effort: a failed poll must not surface an error to the user.
      }
    }

    function schedule(): void {
      if (!running) {
        return;
      }
      const delay = store.active().length > 0 ? ACTIVE_POLL_MS : IDLE_POLL_MS;
      handle = setTimeout(async () => {
        await refresh();
        schedule();
      }, delay);
    }

    return {
      refresh,

      /** Begin polling (idempotent). Owned by the app shell for the session. */
      start(): void {
        if (running) {
          return;
        }
        running = true;
        void refresh().then(() => schedule());
      },

      stop(): void {
        running = false;
        if (handle !== null) {
          clearTimeout(handle);
          handle = null;
        }
      },
    };
  }),
);
