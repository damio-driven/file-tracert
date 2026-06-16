import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { PagedResult, SearchRequest, SearchResultDto } from '../models/catalog.models';

@Injectable({ providedIn: 'root' })
export class SearchApi {
  private readonly http = inject(HttpClient);

  search(req: SearchRequest): Observable<PagedResult<SearchResultDto>> {
    return this.http.post<PagedResult<SearchResultDto>>('/api/search', req);
  }
}
