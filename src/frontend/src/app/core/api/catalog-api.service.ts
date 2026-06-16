import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { CatalogChildrenDto } from '../models/catalog.models';

@Injectable({ providedIn: 'root' })
export class CatalogApi {
  private readonly http = inject(HttpClient);

  children(
    volumeId: number,
    directoryId: number | null,
    skip = 0,
    take = 50,
  ): Observable<CatalogChildrenDto> {
    let params = new HttpParams().set('skip', skip).set('take', take);
    if (directoryId !== null) params = params.set('directoryId', directoryId);
    return this.http.get<CatalogChildrenDto>(`/api/catalog/${volumeId}/children`, { params });
  }
}
