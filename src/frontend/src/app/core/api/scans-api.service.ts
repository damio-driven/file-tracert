import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { ScanStatusDto } from '../models/catalog.models';

/** Typed client for the scan-progress polling endpoint. */
@Injectable({ providedIn: 'root' })
export class ScansApi {
  private readonly http = inject(HttpClient);

  /** Scans currently in flight; empty when nothing is scanning. */
  status(): Observable<ScanStatusDto[]> {
    return this.http.get<ScanStatusDto[]>('/api/scans/status');
  }
}
