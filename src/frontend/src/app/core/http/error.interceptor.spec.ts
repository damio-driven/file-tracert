import {
  HttpClient, HttpContext, HttpErrorResponse, provideHttpClient, withInterceptors,
} from '@angular/common/http';
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

  // C17 — the interceptor used to replace the failure with a fresh `Error`, which made every
  // `instanceof HttpErrorResponse` downstream false and the structured handling of a 400 dead
  // code. What the caller receives has to be the response itself: status and body included.
  it('rethrows the original HttpErrorResponse, status and body intact', () => {
    const { http, httpMock } = setup();

    let caught: unknown = null;
    http.post('/api/operations/enqueue-batch', []).subscribe({
      next: () => {},
      error: (e) => (caught = e),
    });
    httpMock.expectOne('/api/operations/enqueue-batch').flush(
      { error: 'Elemento 2 di 3: File 999 not found.' },
      { status: 400, statusText: 'Bad Request' },
    );

    expect(caught).toBeInstanceOf(HttpErrorResponse);
    const err = caught as HttpErrorResponse;
    expect(err.status).toBe(400);
    expect(err.error).toEqual({ error: 'Elemento 2 di 3: File 999 not found.' });
  });

  // The backend answers `{ error: … }`. Reading `err.error.message` instead meant the user
  // always saw the raw "Http failure response … 400".
  it('shows the message the backend actually sent, not the transport failure', () => {
    const { http, httpMock, toast } = setup();

    http.post('/api/operations/enqueue', {}).subscribe({ next: () => {}, error: () => {} });
    httpMock.expectOne('/api/operations/enqueue').flush(
      { error: "La cartella 'Foto' si trova già in questa posizione." },
      { status: 400, statusText: 'Bad Request' },
    );

    expect(toast.toasts()[0].message).toBe("La cartella 'Foto' si trova già in questa posizione.");
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
