import { InjectionToken } from '@angular/core';
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';

/**
 * The slice of `HubConnection` the app actually drives. Narrow on purpose: it keeps the
 * service honest about what it depends on and lets a test drive the lifecycle without a
 * socket.
 */
export interface HubLike {
  on(method: string, handler: (payload: never) => void): void;
  start(): Promise<void>;
  onreconnecting(callback: () => void): void;
  onreconnected(callback: () => void): void;
  onclose(callback: () => void): void;
}

export type HubConnectionFactory = (url: string) => HubLike;

/**
 * Automatic-reconnect ramp: immediate, then backing off to 20s. When it runs out SignalR
 * gives up and fires `onclose`; `RealtimeService` takes over from there with its own slow
 * retry, so a Host that is down for a minute still gets picked up without a page reload.
 */
const RECONNECT_DELAYS_MS = [0, 2_000, 5_000, 10_000, 20_000];

/**
 * Seam for building the connection. The default builds the real SignalR client; tests
 * substitute a fake and assert on the url they were handed (that is where the token lives).
 */
export const HUB_CONNECTION_FACTORY = new InjectionToken<HubConnectionFactory>(
  'ft.hubConnectionFactory',
  {
    providedIn: 'root',
    factory: (): HubConnectionFactory => (url: string) =>
      new HubConnectionBuilder()
        .withUrl(url)
        .withAutomaticReconnect(RECONNECT_DELAYS_MS)
        .configureLogging(LogLevel.Warning)
        .build(),
  },
);
