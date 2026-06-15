import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ToastService } from './toast.service';

function newService(): ToastService {
  TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  return TestBed.inject(ToastService);
}

describe('ToastService', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('adds a toast with the given severity', () => {
    const svc = newService();
    svc.show('hello', 'info');

    expect(svc.toasts()).toHaveLength(1);
    expect(svc.toasts()[0]).toMatchObject({ message: 'hello', severity: 'info' });
  });

  it('error() raises an error toast', () => {
    const svc = newService();
    svc.error('boom');

    expect(svc.toasts()[0].severity).toBe('error');
  });

  it('dismiss removes a toast by id', () => {
    const svc = newService();
    const id = svc.show('bye');
    svc.dismiss(id);

    expect(svc.toasts()).toHaveLength(0);
  });

  it('auto-dismisses after its TTL', () => {
    const svc = newService();
    svc.show('temp', 'info'); // info TTL = 4s

    vi.advanceTimersByTime(4001);

    expect(svc.toasts()).toHaveLength(0);
  });
});
