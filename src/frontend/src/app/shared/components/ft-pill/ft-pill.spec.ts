import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { FtPill } from './ft-pill';

describe('FtPill', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [FtPill],
      providers: [provideZonelessChangeDetection()],
    }),
  );

  it('maps the variant input to a host class', async () => {
    const fixture = TestBed.createComponent(FtPill);
    fixture.componentRef.setInput('variant', 'block');
    await fixture.whenStable();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.classList).toContain('ft-pill--block');
    expect(host.querySelector('.dot')).not.toBeNull();
  });

  it('defaults to the idle variant', async () => {
    const fixture = TestBed.createComponent(FtPill);
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).classList).toContain('ft-pill--idle');
  });
});
