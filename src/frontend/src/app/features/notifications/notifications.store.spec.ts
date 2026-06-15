import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';

import { NotificationsApi } from '../../core/api/notifications-api.service';
import { NotificationDto } from '../../core/models/catalog.models';
import { NotificationsStore } from './notifications.store';

function notif(id: number, overrides: Partial<NotificationDto> = {}): NotificationDto {
  return {
    id,
    timestampUtc: '2026-06-01T00:00:00Z',
    severity: 'Error',
    source: 'Scan',
    title: `t${id}`,
    message: `m${id}`,
    volumeId: null,
    isRead: false,
    isDismissed: false,
    ...overrides,
  };
}

function configure(api: Partial<NotificationsApi>) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), { provide: NotificationsApi, useValue: api }],
  });
  return TestBed.inject(NotificationsStore);
}

describe('NotificationsStore', () => {
  it('loads the list and unread count', async () => {
    const store = configure({
      list: () => of({ items: [notif(1), notif(2, { isRead: true })], totalCount: 2, skip: 0, take: 50 }),
      unreadCount: () => of({ unread: 1 }),
    });

    await store.loadList();

    expect(store.items()).toHaveLength(2);
    expect(store.unread()).toBe(1);
    expect(store.hasUnread()).toBe(true);
    expect(store.loading()).toBe(false);
  });

  it('marks one read and refreshes the count', async () => {
    const markRead = vi.fn(() => of(undefined));
    const store = configure({
      list: () => of({ items: [notif(1)], totalCount: 1, skip: 0, take: 50 }),
      unreadCount: vi.fn().mockReturnValueOnce(of({ unread: 1 })).mockReturnValueOnce(of({ unread: 0 })),
      markRead,
    });

    await store.loadList();
    await store.markRead(1);

    expect(markRead).toHaveBeenCalledWith(1);
    expect(store.items()[0].isRead).toBe(true);
    expect(store.unread()).toBe(0);
  });

  it('dismiss removes it from the list', async () => {
    const dismiss = vi.fn(() => of(undefined));
    const store = configure({
      list: () => of({ items: [notif(1), notif(2)], totalCount: 2, skip: 0, take: 50 }),
      unreadCount: () => of({ unread: 0 }),
      dismiss,
    });

    await store.loadList();
    await store.dismiss(1);

    expect(dismiss).toHaveBeenCalledWith(1);
    expect(store.items().map((n) => n.id)).toEqual([2]);
  });

  it('toggle opens and loads', async () => {
    const store = configure({
      list: () => of({ items: [notif(1)], totalCount: 1, skip: 0, take: 50 }),
      unreadCount: () => of({ unread: 1 }),
    });

    await store.toggle();

    expect(store.open()).toBe(true);
    expect(store.items()).toHaveLength(1);
  });
});
