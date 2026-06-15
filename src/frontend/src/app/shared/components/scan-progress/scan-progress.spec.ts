import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { ScanStatusDto } from '../../../core/models/catalog.models';
import { ScanProgress } from './scan-progress';

function render(status: ScanStatusDto): HTMLElement {
  TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  const fixture = TestBed.createComponent(ScanProgress);
  fixture.componentRef.setInput('status', status);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

const base: ScanStatusDto = {
  volumeId: 1,
  label: 'Data',
  phase: 'Enumerating',
  itemsSeen: 142_000,
  itemsWritten: 0,
  currentRoot: null,
  startedUtc: '2026-06-15T10:00:00Z',
  updatedUtc: '2026-06-15T10:00:05Z',
};

describe('ScanProgress', () => {
  it('shows the enumeration phase with the seen count', () => {
    const el = render(base);
    expect(el.querySelector('.sp-label')?.textContent).toContain('enumerazione');
    expect(el.textContent).toContain('142.000');
  });

  it('shows the written count during the writing phase', () => {
    const el = render({ ...base, phase: 'Writing', itemsWritten: 80_000 });
    expect(el.querySelector('.sp-label')?.textContent).toContain('scrittura indice');
    expect(el.textContent).toContain('80.000');
  });

  it('omits the count when nothing has been seen yet', () => {
    const el = render({ ...base, itemsSeen: 0 });
    expect(el.querySelector('.sp-count')).toBeNull();
  });
});
