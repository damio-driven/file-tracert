import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { vi } from 'vitest';

import { ScansApi } from '../../core/api/scans-api.service';
import { VolumesApi } from '../../core/api/volumes-api.service';
import { VolumeDetailDto, VolumeDto } from '../../core/models/catalog.models';
import { Volumes } from './volumes';

const volume: VolumeDto = {
  id: 1,
  volumeGuid: '\\\\?\\Volume{x}\\',
  label: 'SSD Lavoro',
  currentLetter: 'D:',
  fileSystem: 'NTFS',
  isRemovable: false,
  isOnline: true,
  lastSeenUtc: '2026-06-12T09:00:00Z',
  capacityBytes: 1000,
  freeBytes: 300,
  fileCount: 5,
  lastFullScanUtc: null,
  dataIsLive: true,
  kind: 'Fixed',
  isCatalogable: true,
};

const cloud: VolumeDto = {
  ...volume,
  id: 2,
  label: 'Google Drive',
  isCatalogable: false,
  kind: 'Cloud',
};

const detail: VolumeDetailDto = {
  ...volume,
  serialNumber: 'B83A-77F1',
  physicalDiskId: '\\\\.\\PHYSICALDRIVE2',
  lastUsn: 44_821_330,
  scanEngine: 'UsnJournal',
  watchedRoots: [{ id: 1, relativePath: '\\Foto', isActive: true, effectiveFilter: 'Immagini' }],
  directoryCount: 2,
  indexedBytes: 700,
};

describe('Volumes screen', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [Volumes],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        {
          provide: VolumesApi,
          useValue: {
            list: () => of([volume, cloud]),
            detail: () => of(detail),
            rescan: () => of(undefined),
            setCatalogable: () => of(undefined),
          },
        },
        { provide: ScansApi, useValue: { status: () => of([]) } },
      ],
    }).compileComponents();
  });

  it('lists catalogable volumes and auto-selects the first detail', async () => {
    const fixture = TestBed.createComponent(Volumes);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('.ft-h1')?.textContent).toContain('Volumi');
    expect(el.querySelector('.vrow')?.textContent).toContain('SSD Lavoro');
    // Auto-selected detail shows the GUID and USN checkpoint.
    expect(el.textContent).toContain('B83A-77F1');
    expect(el.textContent).toContain('44.821.330');
  });

  it('says what each index figure counts instead of pairing two perimeters', async () => {
    const fixture = TestBed.createComponent(Volumes);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    const rowLabelled = (label: string) =>
      [...el.querySelectorAll('.ft-table--kv tr')].find(
        (r) => r.querySelector('td')?.textContent?.trim() === label,
      );

    // The file figure answers to the filter, so it says so. A root switched off takes it to zero.
    const index = rowLabelled('Indice');
    expect(index?.textContent).toContain('5 file inclusi');
    expect(index?.textContent).not.toContain('cartelle');

    // The folder figure does not: a folder on disk is a folder, indexed content or not. Sitting
    // next to the file count after a middot, the pair read as one census and stopped being true.
    const structure = rowLabelled('Struttura');
    expect(structure?.textContent).toContain("2 cartelle nell'albero");
    expect(structure?.textContent).toContain('senza file inclusi');
  });

  it('shows an exclude button in the detail panel for catalogable volumes', async () => {
    const setCatalogableSpy = vi.fn(() => of(undefined));
    await TestBed.configureTestingModule({
      imports: [Volumes],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        {
          provide: VolumesApi,
          useValue: {
            list: () => of([volume, cloud]),
            detail: () => of(detail),
            rescan: () => of(undefined),
            setCatalogable: setCatalogableSpy,
          },
        },
        { provide: ScansApi, useValue: { status: () => of([]) } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(Volumes);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    // Detail panel auto-selected volume 1; should have an exclude button.
    const excludeBtn = [...el.querySelectorAll('.actions button')].find((b) =>
      b.textContent?.trim().startsWith('Escludi'),
    ) as HTMLButtonElement;

    expect(excludeBtn).toBeTruthy();
    excludeBtn.click();
    await fixture.whenStable();

    expect(setCatalogableSpy).toHaveBeenCalledWith(1, false);
  });

  it('keeps an excluded cloud volume out of the main list, behind the toggle', async () => {
    const fixture = TestBed.createComponent(Volumes);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    // The cloud volume is not in the catalogable rows...
    const mainRows = el.querySelectorAll('.vlist:not(.vlist--excluded) .vrow');
    expect([...mainRows].some((r) => r.textContent?.includes('Google Drive'))).toBe(false);

    // ...but the "Sistema / esclusi" section counts it and can reveal it.
    const toggle = el.querySelector('.excluded-toggle') as HTMLButtonElement;
    expect(toggle).toBeTruthy();
    expect(toggle.textContent).toContain('1');

    toggle.click();
    await fixture.whenStable();
    expect(el.querySelector('.vlist--excluded')?.textContent).toContain('Google Drive');
  });
});
