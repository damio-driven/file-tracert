import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { of } from 'rxjs';

import { SetupApi } from '../../core/api/setup-api.service';
import { VolumesApi } from '../../core/api/volumes-api.service';
import { Setup } from './setup';

describe('Setup', () => {
  it('renders the empty-state when no online volume exists', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: VolumesApi, useValue: { list: () => of([]) } },
        { provide: SetupApi, useValue: { browse: () => of([]), getFilter: () => of({ allowedExtensions: [], excludedPaths: [] }) } },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({}) } } },
      ],
    });
    const fixture = TestBed.createComponent(Setup);
    await fixture.componentInstance.ngOnInit();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Nessun volume online');
  });
});
