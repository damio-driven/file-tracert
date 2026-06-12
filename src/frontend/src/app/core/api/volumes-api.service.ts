import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { VolumeDetailDto, VolumeDto } from '../models/catalog.models';

/** Typed client for the Volumes endpoints. */
@Injectable({ providedIn: 'root' })
export class VolumesApi {
  private readonly http = inject(HttpClient);

  list(): Observable<VolumeDto[]> {
    return this.http.get<VolumeDto[]>('/api/volumes');
  }

  detail(id: number): Observable<VolumeDetailDto> {
    return this.http.get<VolumeDetailDto>(`/api/volumes/${id}`);
  }

  /** Queue a full rescan. Returns 202; no body. */
  rescan(id: number): Observable<void> {
    return this.http.post<void>(`/api/volumes/${id}/rescan`, null);
  }
}
