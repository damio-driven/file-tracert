import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

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
  isStale: false,
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
        {
          provide: VolumesApi,
          useValue: { list: () => of([volume]), detail: () => of(detail), rescan: () => of(undefined) },
        },
      ],
    }).compileComponents();
  });

  it('lists volumes and auto-selects the first detail', async () => {
    const fixture = TestBed.createComponent(Volumes);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('.ft-h1')?.textContent).toContain('Volumi');
    expect(el.querySelector('.vrow')?.textContent).toContain('SSD Lavoro');
    // Auto-selected detail shows the GUID and USN checkpoint.
    expect(el.textContent).toContain('B83A-77F1');
    expect(el.textContent).toContain('44.821.330');
  });
});
