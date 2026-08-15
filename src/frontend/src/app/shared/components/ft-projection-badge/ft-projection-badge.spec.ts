import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { FtProjectionBadge } from './ft-projection-badge';

describe('FtProjectionBadge', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [FtProjectionBadge],
      providers: [provideZonelessChangeDetection(), provideRouter([])],
    }),
  );

  async function render(state: string, jobId: number | null = null) {
    const fixture = TestBed.createComponent(FtProjectionBadge);
    fixture.componentRef.setInput('state', state);
    fixture.componentRef.setInput('jobId', jobId);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('renders nothing when nothing is queued on the row', async () => {
    const host = await render('None');
    expect(host.textContent?.trim()).toBe('');
  });

  it.each([
    ['PendingCreate', 'In creazione'],
    ['PendingRename', 'In rinomina'],
    ['PendingMove', 'In spostamento'],
  ])('labels %s in Italian, not with the raw enum name', async (state, label) => {
    const host = await render(state);
    expect(host.textContent).toContain(label);
    expect(host.textContent).not.toContain(state);
  });

  it('links to the owning job in the queue', async () => {
    const host = await render('PendingMove', 42);
    const link = host.querySelector('a');
    expect(link).not.toBeNull();
    expect(link!.getAttribute('href')).toBe('/queue?job=42');
  });

  it('stays inert when no job id is known, rather than linking nowhere', async () => {
    const host = await render('PendingRename');
    expect(host.querySelector('a')).toBeNull();
    expect(host.querySelector('.badge')).not.toBeNull();
  });
});
