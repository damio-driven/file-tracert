import { HttpClient, HttpContext, provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';

import { SUPPRESS_ERROR_TOAST, errorInterceptor } from './error.interceptor';
import { ToastService } from '../toast/toast.service';

function setup() {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(withInterceptors([errorInterceptor])),
      provideHttpClientTesting(),
    ],
  });
  return {
    http: TestBed.inject(HttpClient),
    httpMock: TestBed.inject(HttpTestingController),
    toast: TestBed.inject(ToastService),
  };
}

describe('errorInterceptor', () => {
  it('raises an error toast on a failed request', () => {
    const { http, httpMock, toast } = setup();

    http.get('/api/things').subscribe({ next: () => {}, error: () => {} });
    httpMock.expectOne('/api/things').flush('nope', { status: 500, statusText: 'Server Error' });

    expect(toast.toasts()).toHaveLength(1);
    expect(toast.toasts()[0].severity).toBe('error');
  });

  it('maps an unreachable service (status 0) to a friendly message', () => {
    const { http, httpMock, toast } = setup();

    http.get('/api/things').subscribe({ next: () => {}, error: () => {} });
    httpMock.expectOne('/api/things').error(new ProgressEvent('error'), { status: 0 });

    expect(toast.toasts()[0].message).toBe('Servizio non raggiungibile');
  });

  it('suppresses the toast when the request opts out', () => {
    const { http, httpMock, toast } = setup();

    http
      .get('/api/poll', { context: new HttpContext().set(SUPPRESS_ERROR_TOAST, true) })
      .subscribe({ next: () => {}, error: () => {} });
    httpMock.expectOne('/api/poll').flush('nope', { status: 500, statusText: 'Server Error' });

    expect(toast.toasts()).toHaveLength(0);
  });
});
