import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { LogEntryDto, LogLevelDto, PagedResult } from '../models/catalog.models';

export interface LogQueryParams {
  skip: number;
  take: number;
  level?: string | null;
  category?: string | null;
  search?: string | null;
}

/** Typed client for the logs API (paged read + runtime level control). */
@Injectable({ providedIn: 'root' })
export class LogsApi {
  private readonly http = inject(HttpClient);

  getLogs(query: LogQueryParams): Observable<PagedResult<LogEntryDto>> {
    let params = new HttpParams().set('skip', query.skip).set('take', query.take);
    if (query.level) {
      params = params.set('level', query.level);
    }
    if (query.category?.trim()) {
      params = params.set('category', query.category.trim());
    }
    if (query.search?.trim()) {
      params = params.set('search', query.search.trim());
    }
    return this.http.get<PagedResult<LogEntryDto>>('/api/logs', { params });
  }

  getLevel(): Observable<LogLevelDto> {
    return this.http.get<LogLevelDto>('/api/logs/level');
  }

  setLevel(level: string): Observable<LogLevelDto> {
    return this.http.put<LogLevelDto>('/api/logs/level', { level });
  }
}
