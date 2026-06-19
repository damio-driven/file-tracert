import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import {
  CreateJobRequest,
  FeasibilityResult,
  OperationJobDto,
  PagedResult,
} from '../models/catalog.models';

@Injectable({ providedIn: 'root' })
export class QueueApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/operations';

  list(skip = 0, take = 50): Observable<PagedResult<OperationJobDto>> {
    const params = new HttpParams().set('skip', skip).set('take', take);
    return this.http.get<PagedResult<OperationJobDto>>(this.base, { params });
  }

  enqueue(req: CreateJobRequest): Observable<OperationJobDto> {
    return this.http.post<OperationJobDto>(`${this.base}/enqueue`, req);
  }

  preview(req: CreateJobRequest): Observable<FeasibilityResult> {
    return this.http.post<FeasibilityResult>(`${this.base}/preview`, req);
  }

  cancel(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/${id}`);
  }
}
