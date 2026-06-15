import { HttpClient, HttpContext, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { NotificationCountDto, NotificationDto, PagedResult } from '../models/catalog.models';
import { SUPPRESS_ERROR_TOAST } from '../http/error.interceptor';

/** Typed client for the notifications endpoints. */
@Injectable({ providedIn: 'root' })
export class NotificationsApi {
  private readonly http = inject(HttpClient);

  list(skip = 0, take = 50, unread = false): Observable<PagedResult<NotificationDto>> {
    const params = new HttpParams()
      .set('skip', skip)
      .set('take', take)
      .set('unread', unread);
    return this.http.get<PagedResult<NotificationDto>>('/api/notifications', { params });
  }

  unreadCount(): Observable<NotificationCountDto> {
    // Background tick: a transient failure must not raise a toast.
    return this.http.get<NotificationCountDto>('/api/notifications/unread-count', {
      context: new HttpContext().set(SUPPRESS_ERROR_TOAST, true),
    });
  }

  markRead(id: number): Observable<void> {
    return this.http.post<void>(`/api/notifications/${id}/read`, {});
  }

  dismiss(id: number): Observable<void> {
    return this.http.post<void>(`/api/notifications/${id}/dismiss`, {});
  }
}
