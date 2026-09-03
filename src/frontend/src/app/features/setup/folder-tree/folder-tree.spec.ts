import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { vi } from 'vitest';

import { SetupApi } from '../../../core/api/setup-api.service';
import { VolumesApi } from '../../../core/api/volumes-api.service';
import { SetupStore } from '../setup.store';
import { FolderTree } from './folder-tree';

describe('FolderTree', () => {
  it('renders the root folders after init', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: SetupApi, useValue: { browse: () => of({ items: [{ name: 'Foto', relativePath: 'Foto', hasChildren: false }], totalCount: 1, skip: 0, take: 50 }) } },
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

describe('FolderTree paging (step 17)', () => {
  it('offers the next page only while the disk holds more folders than listed, and appends it', async () => {
    const browse = vi.fn((_v: number, _path: string, skip = 0) => of(skip === 0
      ? { items: [{ name: 'Alpha', relativePath: 'Alpha', hasChildren: false }], totalCount: 2, skip: 0, take: 50 }
      : { items: [{ name: 'Beta', relativePath: 'Beta', hasChildren: false }], totalCount: 2, skip: 1, take: 50 }));
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: SetupApi, useValue: { browse } },
        { provide: VolumesApi, useValue: {} },
      ],
    });
    const store = TestBed.inject(SetupStore);
    store.init(1);
    await store.loadFolders('');

    const fixture = TestBed.createComponent(FolderTree);
    fixture.detectChanges();
    const more = fixture.nativeElement.querySelector('button.more') as HTMLButtonElement | null;
    expect(more).not.toBeNull();
    expect(more!.textContent).toContain('Mostra altre cartelle');
    expect(more!.textContent).toContain('1 di 2');

    more!.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(browse).toHaveBeenLastCalledWith(1, '', 1);
    expect(fixture.nativeElement.textContent).toContain('Alpha');
    expect(fixture.nativeElement.textContent).toContain('Beta');
    expect(fixture.nativeElement.querySelector('button.more')).toBeNull();
  });
});
