import { Injectable, inject, signal } from '@angular/core';

import { RuntimeConfigService } from '../config/runtime-config.service';
import { Logger } from '../logging/logger.service';
import { HUB_CONNECTION_FACTORY, HubLike } from './hub-connection.factory';
import { RealtimeMessageMap, RealtimeMethod, RealtimeStatus } from './realtime.models';

const HUB_URL = '/hubs/events';

/** How long to wait before re-trying a connection SignalR has already given up on. */
const OFFLINE_RETRY_MS = 15_000;

/**
 * The SignalR client (§7). Server → client only: the hub has no methods to call, so this
 * service is a subscription surface plus a connection state machine, nothing else.
 *
 * It knows nothing about stores or screens — `RealtimeBridge` owns that wiring. What it
 * owns is the honesty of the connection: `status` is what the shell shows, and
 * `onReconnected` is how the screens refill the gap, because messages emitted while the
 * socket was down are never replayed.
 */
@Injectable({ providedIn: 'root' })
export class RealtimeService {
  private readonly config = inject(RuntimeConfigService);
  private readonly createConnection = inject(HUB_CONNECTION_FACTORY);
  private readonly logger = inject(Logger);

  private readonly state = signal<RealtimeStatus>('connecting');
  /** `connecting | connected | reconnecting | offline`, for the shell indicator. */
  readonly status = this.state.asReadonly();

  private connection: HubLike | null = null;
  private readonly handlers = new Map<string, (payload: never) => void>();
  private readonly reconnectedCallbacks: (() => void)[] = [];
  private retryHandle: ReturnType<typeof setTimeout> | null = null;
  private attempts = 0;
  private starting = false;
  private stopped = false;

  /**
   * Subscribe to one hub message. Call before {@link start}: handlers are registered on the
   * connection at build time, and a handler added later would miss everything until then.
   */
  on<K extends RealtimeMethod>(method: K, handler: (payload: RealtimeMessageMap[K]) => void): void {
    this.handlers.set(method, handler as (payload: never) => void);
  }

  /** Runs after every reconnection (never after the first connect: nothing was missed yet). */
  onReconnected(callback: () => void): void {
    this.reconnectedCallbacks.push(callback);
  }

  /** Opens the connection. Safe to call once; the app initializer owns the single call. */
  async start(): Promise<void> {
    this.stopped = false;
    await this.connect();
  }

  /** "Riconnetti": the user asking now instead of waiting out the retry timer. */
  async reconnectNow(): Promise<void> {
    this.clearRetry();
    this.stopped = false;
    this.state.set('reconnecting');
    await this.connect();
  }

  /** Closes the connection for good (no retry). */
  async stop(): Promise<void> {
    this.stopped = true;
    this.clearRetry();
    const connection = this.connection;
    this.connection = null;
    if (connection) {
      await connection.stop().catch((e: unknown) => this.logger.error(`realtime: stop failed — ${describe(e)}`));
    }
    this.state.set('offline');
  }

  private async connect(): Promise<void> {
    if (this.starting || this.stopped) {
      return;
    }
    this.starting = true;
    // Every attempt after the first is a recovery, whether we lost a live connection or
    // never got one: either way the app has been running blind and has to refill.
    const isRecovery = this.attempts++ > 0;
    try {
      const connection = this.connection ?? this.build();
      await connection.start();
      this.connection = connection;
      this.state.set('connected');
      if (isRecovery) {
        this.fireReconnected();
      }
    } catch (e) {
      // Resilience, not silence (§9): the failure is logged in full and the shell says
      // "offline" — the user is never shown stale data dressed up as live.
      this.logger.error(`realtime: connection failed — ${describe(e)}`);
      this.state.set('offline');
      this.scheduleRetry();
    } finally {
      this.starting = false;
    }
  }

  private build(): HubLike {
    const connection = this.createConnection(this.hubUrl());
    for (const [method, handler] of this.handlers) {
      connection.on(method, handler);
    }
    connection.onreconnecting(() => this.state.set('reconnecting'));
    connection.onreconnected(() => {
      this.state.set('connected');
      this.fireReconnected();
    });
    connection.onclose(() => {
      // SignalR has exhausted its own ramp. From here the retry is ours.
      if (this.stopped) {
        return;
      }
      this.state.set('offline');
      this.scheduleRetry();
    });
    return connection;
  }

  /**
   * The token rides in the query string, not a header: the browser's WebSocket handshake
   * cannot carry custom headers, which is exactly why the Host accepts `?access_token=`
   * on `/hubs/*`. With no token resolved we still connect — the 401 that follows is a
   * clearer signal than a client that quietly never tries.
   */
  private hubUrl(): string {
    const token = this.config.token;
    return token ? `${HUB_URL}?access_token=${encodeURIComponent(token)}` : HUB_URL;
  }

  private scheduleRetry(): void {
    if (this.retryHandle !== null || this.stopped) {
      return;
    }
    this.retryHandle = setTimeout(() => {
      this.retryHandle = null;
      void this.connect();
    }, OFFLINE_RETRY_MS);
  }

  private clearRetry(): void {
    if (this.retryHandle !== null) {
      clearTimeout(this.retryHandle);
      this.retryHandle = null;
    }
  }

  private fireReconnected(): void {
    for (const callback of this.reconnectedCallbacks) {
      try {
        callback();
      } catch (e) {
        this.logger.error(`realtime: reconnect refresh failed — ${describe(e)}`);
      }
    }
  }
}

function describe(e: unknown): string {
  return e instanceof Error ? `${e.message}\n${e.stack ?? ''}` : String(e);
}
