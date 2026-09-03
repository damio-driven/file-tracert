import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import {
  CreateWatchedRootRequest,
  FilterSettingsDto,
  FolderNodeDto,
  PagedResult,
  ReconcileResultDto,
  UpdateWatchedRootRequest,
  WatchedRootDto,
  WatchedRootUpdateResponse,
} from '../models/catalog.models';

/** Typed client for the setup (browse / watched-roots / filter) endpoints. */
@Injectable({ providedIn: 'root' })
export class SetupApi {
  private readonly http = inject(HttpClient);

  /** One page (50) of the folders at `path` on the real disk, from `skip` (step 17). */
  browse(volumeId: number, path: string, skip = 0): Observable<PagedResult<FolderNodeDto>> {
    return this.http.get<PagedResult<FolderNodeDto>>(
      `/api/volumes/${volumeId}/folders`, { params: { path, skip, take: 50 } });
  }

  createRoot(volumeId: number, body: CreateWatchedRootRequest): Observable<WatchedRootDto> {
    return this.http.post<WatchedRootDto>(`/api/volumes/${volumeId}/watched-roots`, body);
  }

  updateRoot(rootId: number, body: UpdateWatchedRootRequest): Observable<WatchedRootUpdateResponse> {
    return this.http.patch<WatchedRootUpdateResponse>(`/api/watched-roots/${rootId}`, body);
  }

  deleteRoot(rootId: number): Observable<void> {
    return this.http.delete<void>(`/api/watched-roots/${rootId}`);
  }

  getFilter(): Observable<FilterSettingsDto> {
    return this.http.get<FilterSettingsDto>('/api/settings/filter');
  }

  putFilter(body: FilterSettingsDto): Observable<ReconcileResultDto> {
    return this.http.put<ReconcileResultDto>('/api/settings/filter', body);
  }
}
