import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { App } from './app';
import { DashboardApi } from './core/api/dashboard-api.service';
import { NotificationsApi } from './core/api/notifications-api.service';
import { ScansApi } from './core/api/scans-api.service';
import { DashboardStatsDto, ScanStatusDto } from './core/models/catalog.models';

const stats: DashboardStatsDto = {
  totalFiles: 1_214_882,
  totalBytes: 3_400_000_000_000,
  volumesOnline: 3,
  volumesTotal: 4,
  queuedJobs: 0,
  blockedJobs: 0,
  runningJobs: 0,
  pendingBytes: 0,
};

describe('App shell', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: DashboardApi, useValue: { getStats: () => of(stats) } },
        {
          provide: NotificationsApi,
          useValue: {
            unreadCount: () => of({ unread: 0 }),
            list: () => of({ items: [], totalCount: 0, skip: 0, take: 50 }),
          },
        },
        { provide: ScansApi, useValue: { status: () => of([]) } },
      ],
    }).compileComponents();
  });

  it('renders the brand and nav', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('.brand')?.textContent).toContain('FileTracert');
    expect(el.querySelectorAll('.navitem').length).toBeGreaterThanOrEqual(2);
  });

  it('shows the service-active footer once stats load', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('.navfoot')?.textContent).toContain('servizio attivo');
    expect(el.querySelector('.navfoot')?.textContent).toContain('file');
  });

  it('hides the scan flag when nothing is scanning', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('.scan-flag')).toBeNull();
  });
});

describe('App scan indicator', () => {
  const scan: ScanStatusDto = {
    volumeId: 1,
    label: 'Disk',
    phase: 'Writing',
    itemsSeen: 142_000,
    itemsWritten: 80_000,
    currentRoot: null,
    startedUtc: '2026-06-15T10:00:00Z',
    updatedUtc: '2026-06-15T10:00:05Z',
  };

  it('shows the global scan flag while a scan is active', async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: DashboardApi, useValue: { getStats: () => of(stats) } },
        {
          provide: NotificationsApi,
          useValue: {
            unreadCount: () => of({ unread: 0 }),
            list: () => of({ items: [], totalCount: 0, skip: 0, take: 50 }),
          },
        },
        { provide: ScansApi, useValue: { status: () => of([scan]) } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('.scan-flag')?.textContent).toContain('scansione');
  });
});
