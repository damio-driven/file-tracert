import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

import { SetupApi } from '../../../core/api/setup-api.service';
import { VolumesApi } from '../../../core/api/volumes-api.service';
import { SetupStore } from '../setup.store';
import { FolderTree } from './folder-tree';

describe('FolderTree', () => {
  it('renders the root folders after init', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: SetupApi, useValue: { browse: () => of([{ name: 'Foto', relativePath: 'Foto', hasChildren: false }]) } },
        { provide: VolumesApi, useValue: {} },
      ],
    });
    const store = TestBed.inject(SetupStore);
    store.init(1);
    await store.loadFolders('');

    const fixture = TestBed.createComponent(FolderTree);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Foto');
  });
});
