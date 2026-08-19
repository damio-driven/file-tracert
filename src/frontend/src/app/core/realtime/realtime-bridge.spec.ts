import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { vi } from 'vitest';

import { CatalogApi } from '../api/catalog-api.service';
import { DashboardApi } from '../api/dashboard-api.service';
import { NotificationsApi } from '../api/notifications-api.service';
import { QueueApi } from '../api/queue-api.service';
import { SearchApi } from '../api/search-api.service';
import { ScansApi } from '../api/scans-api.service';
import { VolumesApi } from '../api/volumes-api.service';
import { CatalogStore } from '../../features/catalog/catalog.store';
import { DashboardStore } from '../../features/dashboard/dashboard.store';
import { QueueStore } from '../../features/queue/queue.store';
import { ScanStatusStore } from '../../features/scans/scan-status.store';
import { VolumesStore } from '../../features/volumes/volumes.store';
import { RealtimeBridge } from './realtime-bridge';
import { RealtimeMessageMap, RealtimeMethod } from './realtime.models';
import { RealtimeService } from './realtime.service';

const emptyPage = { items: [], totalCount: 0, skip: 0, take: 50 };

/** Stands in for the connection: records the wiring the bridge asks for, then drives it. */
class FakeRealtime {
  readonly handlers = new Map<string, (payload: never) => void>();
  readonly reconnected: (() => void)[] = [];
  startCalls = 0;

  on<K extends RealtimeMethod>(method: K, handler: (payload: RealtimeMessageMap[K]) => void): void {
    this.handlers.set(method, handler as (payload: never) => void);
  }
  onReconnected(callback: () => void): void {
    this.reconnected.push(callback);
  }
  start(): Promise<void> {
    this.startCalls++;
    return Promise.resolve();
  }

  emit<K extends RealtimeMethod>(method: K, payload: RealtimeMessageMap[K]): void {
    this.handlers.get(method)?.(payload as never);
  }
  dropAndRecover(): void {
    for (const callback of this.reconnected) {
      callback();
    }
  }
}

function setup() {
  const realtime = new FakeRealtime();
  const queueList = vi.fn(() => of(emptyPage));
  const volumesList = vi.fn(() => of([]));
  const scanStatus = vi.fn(() => of([]));
  const unreadCount = vi.fn(() => of({ unread: 0 }));
  const stats = vi.fn(() => of({} as never));

  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: RealtimeService, useValue: realtime },
      { provide: QueueApi, useValue: { list: queueList } },
      { provide: VolumesApi, useValue: { list: volumesList } },
      { provide: ScansApi, useValue: { status: scanStatus } },
      { provide: NotificationsApi, useValue: { unreadCount, list: () => of(emptyPage) } },
      { provide: DashboardApi, useValue: { getStats: stats } },
      { provide: CatalogApi, useValue: { children: vi.fn() } },
      { provide: SearchApi, useValue: { search: vi.fn() } },
    ],
  });

  return {
    realtime,
    bridge: TestBed.inject(RealtimeBridge),
    queueList,
    volumesList,
    scanStatus,
    unreadCount,
    stats,
  };
}

describe('RealtimeBridge', () => {
  afterEach(() => {
    vi.useRealTimers();
    TestBed.resetTestingModule();
  });

  it('registers every hub message before opening the connection', async () => {
    const { realtime, bridge } = setup();

    await bridge.start();

    expect([...realtime.handlers.keys()].sort()).toEqual([
      'JobProgress',
      'JobStateChanged',
      'NotificationRaised',
      'ProjectionChanged',
      'ScanProgress',
      'VolumeStatusChanged',
    ]);
    expect(realtime.startCalls).toBe(1);
  });

  it('connects once even if start is called again', async () => {
    const { realtime, bridge } = setup();

    await bridge.start();
    await bridge.start();

    expect(realtime.startCalls).toBe(1);
  });

  it('routes a message to the store that owns it', async () => {
    const { realtime, bridge } = setup();
    await bridge.start();
    const queue = TestBed.inject(QueueStore);
    const volumes = TestBed.inject(VolumesStore);
    const scans = TestBed.inject(ScanStatusStore);
    await queue.load();

    realtime.emit('JobProgress', { jobId: 1, bytesProcessed: 5, totalBytes: 10 });
    realtime.emit('VolumeStatusChanged', {
      volumeId: 1, isOnline: true, freeBytesLastKnown: 1, lastSeenUtc: '2026-08-18T00:00:00Z',
    });
    realtime.emit('ScanProgress', {
      volumeId: 4, label: null, phase: 'Writing', itemsSeen: 1, itemsWritten: 0,
      currentRoot: null, startedUtc: '2026-08-18T00:00:00Z', updatedUtc: '2026-08-18T00:00:01Z',
    });

    expect(volumes.volumes()).toEqual([]);
    expect(scans.activeCount()).toBe(1);
  });

  it('refills the visible screens after a reconnection, because nothing is replayed', async () => {
    const { realtime, bridge, scanStatus, unreadCount, stats } = setup();
    await bridge.start();
    scanStatus.mockClear();
    unreadCount.mockClear();
    stats.mockClear();

    realtime.dropAndRecover();

    expect(stats).toHaveBeenCalledTimes(1);
    expect(scanStatus).toHaveBeenCalledTimes(1);
    expect(unreadCount).toHaveBeenCalledTimes(1);
  });

  it('does not fetch a screen the user never opened', async () => {
    const { realtime, bridge, queueList, volumesList } = setup();
    await bridge.start();

    realtime.dropAndRecover();

    expect(queueList).not.toHaveBeenCalled();
    expect(volumesList).not.toHaveBeenCalled();
  });

  it('does refetch the queue once it has been opened', async () => {
    const { realtime, bridge, queueList } = setup();
    await bridge.start();
    await TestBed.inject(QueueStore).load();
    queueList.mockClear();

    realtime.dropAndRecover();

    expect(queueList).toHaveBeenCalledTimes(1);
  });

  it('collects a burst of overlay changes into one refresh', async () => {
    vi.useFakeTimers();
    const { realtime, bridge } = setup();
    await bridge.start();
    const catalog = TestBed.inject(CatalogStore);
    const invalidate = vi.spyOn(catalog, 'invalidate');

    for (let i = 0; i < 50; i++) {
      realtime.emit('ProjectionChanged', { volumeId: 1, jobId: i });
    }
    await vi.advanceTimersByTimeAsync(1_000);

    expect(invalidate).toHaveBeenCalledTimes(1);
    expect(invalidate).toHaveBeenCalledWith(1);
  });

  // C30 — three of the four Dashboard cards count queue jobs, and the payload carries no
  // bytes, so a transition has to reach the store as a re-read (coalesced).
  it('re-reads the dashboard cards after a burst of queue transitions', async () => {
    vi.useFakeTimers();
    const { realtime, bridge, stats } = setup();
    await bridge.start();
    await TestBed.inject(DashboardStore).load();
    stats.mockClear();

    for (let i = 1; i <= 30; i++) {
      realtime.emit('JobStateChanged', {
        jobId: i, state: 'Pending', blockReason: 'None', errorMessage: null,
      });
    }
    await vi.advanceTimersByTimeAsync(2_000);

    expect(stats).toHaveBeenCalledTimes(1);
  });

  it('widens the refresh when the burst spans more than one volume', async () => {
    vi.useFakeTimers();
    const { realtime, bridge } = setup();
    await bridge.start();
    const catalog = TestBed.inject(CatalogStore);
    const invalidate = vi.spyOn(catalog, 'invalidate');

    realtime.emit('ProjectionChanged', { volumeId: 1, jobId: 1 });
    realtime.emit('ProjectionChanged', { volumeId: 2, jobId: 2 });
    await vi.advanceTimersByTimeAsync(1_000);

    expect(invalidate).toHaveBeenCalledTimes(1);
    expect(invalidate).toHaveBeenCalledWith(null);
  });
});
