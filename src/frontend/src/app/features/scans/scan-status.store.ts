import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { firstValueFrom } from 'rxjs';

import { ScansApi } from '../../core/api/scans-api.service';
import { ScanPhase, ScanStatusDto } from '../../core/models/catalog.models';

interface ScanStatusState {
  active: ScanStatusDto[];
}

const initial: ScanStatusState = { active: [] };

/** Phases the tracker sends as its last frame before dropping the volume. */
const TERMINAL_PHASES = new Set<ScanPhase>(['Done', 'Failed']);

/**
 * In-flight scans, fed by the hub's `ScanProgress` (step 10c). There is no polling: the
 * tracker pushes a frame on start, on every phase change and a terminal `Done`/`Failed`
 * one before it drops the volume. `refresh()` survives for the two moments a push cannot
 * cover — the first paint, and the gap after a reconnection.
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
    async function refresh(): Promise<void> {
      try {
        const active = await firstValueFrom(api.status());
        patchState(store, { active });
      } catch {
        // Progress is best-effort: a failed read must not surface an error to the user.
      }
    }

    return {
      refresh,

      /**
       * `ScanProgress` push. Upsert by volume, and drop the volume on the terminal frame:
       * that frame is what tells the UI "finished" apart from "the connection died", so it
       * is applied and then removed, not ignored.
       */
      applyScanProgress(message: ScanStatusDto): void {
        const others = store.active().filter((s) => s.volumeId !== message.volumeId);
        patchState(store, {
          active: TERMINAL_PHASES.has(message.phase) ? others : [...others, message],
        });
      },
    };
  }),
);
