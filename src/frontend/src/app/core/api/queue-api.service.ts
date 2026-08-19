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

  /**
   * A whole selection in one call (C25). The server enqueues all of it or none of it, so a
   * failure leaves nothing behind and repeating the corrected gesture cannot duplicate jobs.
   * Returns the created jobs in request order, each with the state it was born in.
   */
  enqueueBatch(reqs: CreateJobRequest[]): Observable<OperationJobDto[]> {
    return this.http.post<OperationJobDto[]>(`${this.base}/enqueue-batch`, reqs);
  }

  preview(req: CreateJobRequest): Observable<FeasibilityResult> {
    return this.http.post<FeasibilityResult>(`${this.base}/preview`, req);
  }

  /** Feasibility of a whole batch, evaluated by the backend as one aggregated demand. */
  previewBatch(reqs: CreateJobRequest[]): Observable<FeasibilityResult> {
    return this.http.post<FeasibilityResult>(`${this.base}/preview-batch`, reqs);
  }

  cancel(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/${id}`);
  }

  /** Puts a Blocked/Failed job back in queue for another attempt. */
  retry(id: number): Observable<OperationJobDto> {
    return this.http.post<OperationJobDto>(`${this.base}/${id}/retry`, null);
  }
}
