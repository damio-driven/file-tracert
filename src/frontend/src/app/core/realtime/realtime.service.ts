import { Injectable } from '@angular/core';

/**
 * Placeholder for the SignalR client (step 10). The hub will push
 * `VolumeStatusChanged`, `JobProgress`, `ScanProgress`, `ProjectionChanged`…;
 * those handlers will patch the same SignalStores the screens already read, so
 * the UI reacts without any change to the components.
 *
 * Intentionally inert for now: stores fetch on load + manual refresh.
 */
@Injectable({ providedIn: 'root' })
export class RealtimeService {
  // connect(): Promise<void> — wired at step 10 with @microsoft/signalr.
}
