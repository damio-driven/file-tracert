import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { afterEach, describe, expect, it } from 'vitest';

import { NotificationsApi } from '../../../core/api/notifications-api.service';
import { NotificationsStore } from '../../../features/notifications/notifications.store';
import { NotificationsBell } from './notifications-bell';

const page = {
  items: [
    {
      id: 1,
      timestampUtc: '2026-06-01T00:00:00Z',
      severity: 'Error' as const,
      source: 'Scan',
      title: 'Scansione fallita',
      message: 'ACCESS_DENIED',
      volumeId: 3,
      isRead: false,
      isDismissed: false,
    },
  ],
  totalCount: 1,
  skip: 0,
  take: 50,
};

function api() {
  return { list: () => of(page), unreadCount: () => of({ unread: 2 }), markRead: () => of(undefined), dismiss: () => of(undefined) };
}

describe('NotificationsBell', () => {
  afterEach(() => {
    document.querySelectorAll('.cdk-overlay-container').forEach((el) => el.remove());
  });

  it('shows the unread badge from the store count', async () => {
    TestBed.configureTestingModule({
      imports: [NotificationsBell],
      providers: [provideZonelessChangeDetection(), { provide: NotificationsApi, useValue: api() }],
    });
    await TestBed.inject(NotificationsStore).refreshCount();

    const fixture = TestBed.createComponent(NotificationsBell);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('.ft-notif-badge')?.textContent?.trim()).toBe('2');
    expect(el.querySelector('.ft-notif-bell')?.getAttribute('aria-label')).toContain('2 non lette');
  });

  it('opens the panel and lists notifications on click', async () => {
    TestBed.configureTestingModule({
      imports: [NotificationsBell],
      providers: [provideZonelessChangeDetection(), { provide: NotificationsApi, useValue: api() }],
    });

    const fixture = TestBed.createComponent(NotificationsBell);
    await fixture.whenStable();
    const button = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('.ft-notif-bell')!;

    button.click();
    await fixture.whenStable();

    expect(TestBed.inject(NotificationsStore).open()).toBe(true);
    expect(document.body.textContent).toContain('Scansione fallita');
  });
});
