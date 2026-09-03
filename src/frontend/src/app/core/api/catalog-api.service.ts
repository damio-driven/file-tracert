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
    dirSkip = 0,
    dirTake = 50,
  ): Observable<CatalogChildrenDto> {
    // Two axes: `skip`/`take` page the files, `dirSkip`/`dirTake` the subfolders (step 17).
    let params = new HttpParams()
      .set('skip', skip).set('take', take)
      .set('dirSkip', dirSkip).set('dirTake', dirTake);
    if (directoryId !== null) params = params.set('directoryId', directoryId);
    return this.http.get<CatalogChildrenDto>(`/api/catalog/${volumeId}/children`, { params });
  }
}
