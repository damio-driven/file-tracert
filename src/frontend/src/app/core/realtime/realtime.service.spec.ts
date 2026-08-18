import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';

import { RuntimeConfigService } from '../config/runtime-config.service';
import { HUB_CONNECTION_FACTORY, HubLike } from './hub-connection.factory';
import { RealtimeService } from './realtime.service';

/** Minimal stand-in for `HubConnection`: only what the service actually drives. */
class FakeHub implements HubLike {
  readonly handlers = new Map<string, (payload: never) => void>();
  startCalls = 0;
  startResult: Promise<void> = Promise.resolve();

  private reconnecting: (() => void) | null = null;
  private reconnected: (() => void) | null = null;
  private closed: (() => void) | null = null;

  on(method: string, handler: (payload: never) => void): void {
    this.handlers.set(method, handler);
  }
  start(): Promise<void> {
    this.startCalls++;
    return this.startResult;
  }
  onreconnecting(cb: () => void): void {
    this.reconnecting = cb;
  }
  onreconnected(cb: () => void): void {
    this.reconnected = cb;
  }
  onclose(cb: () => void): void {
    this.closed = cb;
  }

  emit(method: string, payload: unknown): void {
    this.handlers.get(method)?.(payload as never);
  }
  fireReconnecting(): void {
    this.reconnecting?.();
  }
  fireReconnected(): void {
    this.reconnected?.();
  }
  fireClose(): void {
    this.closed?.();
  }
}

function setup(token: string | null = 'abc 123') {
  const hub = new FakeHub();
  const urls: string[] = [];

  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: RuntimeConfigService, useValue: { token, load: () => Promise.resolve() } },
      {
        provide: HUB_CONNECTION_FACTORY,
        useValue: (url: string) => {
          urls.push(url);
          return hub;
        },
      },
    ],
  });

  return { hub, urls, service: TestBed.inject(RealtimeService) };
}

describe('RealtimeService', () => {
  afterEach(() => {
    vi.useRealTimers();
    TestBed.resetTestingModule();
  });

  it('builds the hub url with the token in the query string', async () => {
    const { urls, service } = setup('tok en/+');
    await service.start();

    expect(urls).toHaveLength(1);
    expect(urls[0]).toBe(`/hubs/events?access_token=${encodeURIComponent('tok en/+')}`);
  });

  it('connects without a token rather than not connecting at all', async () => {
    const { urls, service } = setup(null);
    await service.start();

    expect(urls[0]).toBe('/hubs/events');
  });

  it('registers every declared handler before starting', async () => {
    const { hub, service } = setup();
    const seen: unknown[] = [];
    service.on('JobProgress', (p) => seen.push(p));
    await service.start();

    hub.emit('JobProgress', { jobId: 7, bytesProcessed: 10, totalBytes: 20 });

    expect(seen).toEqual([{ jobId: 7, bytesProcessed: 10, totalBytes: 20 }]);
  });

  it('is connected after a successful start', async () => {
    const { service } = setup();
    expect(service.status()).toBe('connecting');

    await service.start();

    expect(service.status()).toBe('connected');
  });

  it('tracks reconnecting / reconnected / close', async () => {
    const { hub, service } = setup();
    await service.start();

    hub.fireReconnecting();
    expect(service.status()).toBe('reconnecting');

    hub.fireReconnected();
    expect(service.status()).toBe('connected');

    hub.fireClose();
    expect(service.status()).toBe('offline');
  });

  it('runs the reconnected callbacks so the screens refill what they missed', async () => {
    const { hub, service } = setup();
    const refreshed = vi.fn();
    service.onReconnected(refreshed);
    await service.start();

    expect(refreshed).not.toHaveBeenCalled();

    hub.fireReconnected();

    expect(refreshed).toHaveBeenCalledTimes(1);
  });

  it('goes offline when the very first start fails, and retries on its own', async () => {
    vi.useFakeTimers();
    const { hub, service } = setup();
    const refreshed = vi.fn();
    service.onReconnected(refreshed);
    hub.startResult = Promise.reject(new Error('econnrefused'));

    await service.start();
    expect(service.status()).toBe('offline');

    hub.startResult = Promise.resolve();
    await vi.advanceTimersByTimeAsync(60_000);

    expect(hub.startCalls).toBeGreaterThan(1);
    expect(service.status()).toBe('connected');
    // The app ran blind between the failed handshake and this one: it has to refill too.
    expect(refreshed).toHaveBeenCalledTimes(1);
  });

  it('treats a retry that lands after a drop as a reconnection', async () => {
    vi.useFakeTimers();
    const { hub, service } = setup();
    const refreshed = vi.fn();
    service.onReconnected(refreshed);

    await service.start();
    hub.fireClose();
    expect(service.status()).toBe('offline');

    await vi.advanceTimersByTimeAsync(60_000);

    expect(service.status()).toBe('connected');
    expect(refreshed).toHaveBeenCalledTimes(1);
  });

  it('reconnects on demand without waiting for the retry timer', async () => {
    vi.useFakeTimers();
    const { hub, service } = setup();
    await service.start();
    hub.fireClose();

    await service.reconnectNow();

    expect(service.status()).toBe('connected');
    expect(hub.startCalls).toBe(2);
  });
});
