import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { App } from './app';
import { DashboardApi } from './core/api/dashboard-api.service';
import { DashboardStatsDto } from './core/models/catalog.models';

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
});
