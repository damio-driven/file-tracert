import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

import { DashboardApi } from '../../core/api/dashboard-api.service';
import { ScansApi } from '../../core/api/scans-api.service';
import { VolumesApi } from '../../core/api/volumes-api.service';
import { DashboardStatsDto, VolumeDto } from '../../core/models/catalog.models';
import { Dashboard } from './dashboard';

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

const offline: VolumeDto = {
  id: 9,
  volumeGuid: '\\\\?\\Volume{z}\\',
  label: 'Foto Archivio',
  currentLetter: null,
  fileSystem: 'NTFS',
  isRemovable: true,
  isOnline: false,
  lastSeenUtc: '2026-06-10T09:00:00Z',
  capacityBytes: 1000,
  freeBytes: 128,
  fileCount: 48_054,
  lastFullScanUtc: null,
  dataIsLive: false,
  isStale: true,
  kind: 'Removable',
  isCatalogable: true,
};

describe('Dashboard screen', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [Dashboard],
      providers: [
        provideZonelessChangeDetection(),
        { provide: DashboardApi, useValue: { getStats: () => of(stats) } },
        { provide: VolumesApi, useValue: { list: () => of([offline]) } },
        { provide: ScansApi, useValue: { status: () => of([]) } },
      ],
    }).compileComponents();
  });

  it('renders cards and the stale offline row', async () => {
    const fixture = TestBed.createComponent(Dashboard);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('.ft-h1')?.textContent).toContain('Dashboard');
    expect(el.querySelectorAll('ft-card').length).toBe(4);
    // Offline volume surfaces the dated estimate badge.
    expect(el.querySelector('.ft-stale')?.textContent).toContain('stima');
    expect(el.textContent).toContain('Scollegato');
  });
});
