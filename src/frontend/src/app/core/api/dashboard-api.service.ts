import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { DashboardStatsDto } from '../models/catalog.models';

/** Typed client for the Dashboard endpoint. */
@Injectable({ providedIn: 'root' })
export class DashboardApi {
  private readonly http = inject(HttpClient);

  getStats(): Observable<DashboardStatsDto> {
    return this.http.get<DashboardStatsDto>('/api/dashboard');
  }
}
