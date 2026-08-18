import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';

import { RealtimeStatus } from '../../../core/realtime/realtime.models';
import { RealtimeService } from '../../../core/realtime/realtime.service';
import { ConnectionStatus } from './connection-status';

function setup(initial: RealtimeStatus) {
  const status = signal<RealtimeStatus>(initial);
  const reconnectNow = vi.fn(() => Promise.resolve());

  TestBed.configureTestingModule({
    imports: [ConnectionStatus],
    providers: [
      provideZonelessChangeDetection(),
      { provide: RealtimeService, useValue: { status, reconnectNow } },
    ],
  });

  const fixture = TestBed.createComponent(ConnectionStatus);
  return { fixture, status, reconnectNow, host: fixture.nativeElement as HTMLElement };
}

describe('ConnectionStatus', () => {
  afterEach(() => TestBed.resetTestingModule());

  it.each(['connecting', 'connected'] as RealtimeStatus[])(
    'stays silent while %s: a warning that shows when nothing is wrong teaches the user to ignore it',
    async (state) => {
      const { fixture, host } = setup(state);
      await fixture.whenStable();

      expect(host.querySelector('.conn')).toBeNull();
    },
  );

  it('shows a transient amber flag while reconnecting', async () => {
    const { fixture, host } = setup('reconnecting');
    await fixture.whenStable();

    const flag = host.querySelector('.conn');
    expect(flag).not.toBeNull();
    expect(flag!.classList.contains('conn--offline')).toBe(false);
    expect(flag!.textContent).toContain('riconnessione');
    expect(host.querySelector('button')).toBeNull();
  });

  it('says the data is no longer live once offline, and offers the way back', async () => {
    const { fixture, host, reconnectNow } = setup('offline');
    await fixture.whenStable();

    const flag = host.querySelector('.conn')!;
    expect(flag.classList.contains('conn--offline')).toBe(true);
    expect(flag.textContent).toContain('aggiornamenti in pausa');
    expect(flag.getAttribute('title')).toContain('ultimo dato ricevuto');

    const button = host.querySelector('button')!;
    expect(button.textContent).toContain('Riconnetti');
    button.click();

    expect(reconnectNow).toHaveBeenCalledTimes(1);
  });

  it('follows the connection state without anyone telling it to', async () => {
    const { fixture, host, status } = setup('connected');
    await fixture.whenStable();
    expect(host.querySelector('.conn')).toBeNull();

    status.set('offline');
    await fixture.whenStable();
    expect(host.querySelector('.conn')).not.toBeNull();

    status.set('connected');
    await fixture.whenStable();
    expect(host.querySelector('.conn')).toBeNull();
  });

  it('announces itself politely instead of stealing focus', async () => {
    const { fixture, host } = setup('offline');
    await fixture.whenStable();

    const flag = host.querySelector('.conn')!;
    expect(flag.getAttribute('role')).toBe('status');
    expect(flag.getAttribute('aria-live')).toBe('polite');
  });
});
