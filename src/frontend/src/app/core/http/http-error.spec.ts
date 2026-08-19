import { HttpErrorResponse } from '@angular/common/http';
import { describe, expect, it } from 'vitest';

import { httpErrorMessage } from './http-error';

function response(status: number, body: unknown): HttpErrorResponse {
  return new HttpErrorResponse({ status, statusText: 'Error', error: body, url: '/api/things' });
}

describe('httpErrorMessage', () => {
  it("reads the backend's { error } shape", () => {
    expect(httpErrorMessage(response(400, { error: 'Il nome non può essere vuoto.' })))
      .toBe('Il nome non può essere vuoto.');
  });

  it('prefers the server message over the generic label for the status', () => {
    // A 404 that explains itself is more useful than "Risorsa non trovata".
    expect(httpErrorMessage(response(404, { error: 'Job 12 not found.' })))
      .toBe('Job 12 not found.');
  });

  it('falls back to a label per status when the body says nothing', () => {
    expect(httpErrorMessage(response(401, null))).toBe('Token non valido o mancante');
    expect(httpErrorMessage(response(404, null))).toBe('Risorsa non trovata');
    expect(httpErrorMessage(response(500, ''))).toBe('Errore 500');
  });

  it('names the unreachable service instead of quoting a meaningless status 0', () => {
    expect(httpErrorMessage(response(0, null))).toBe('Servizio non raggiungibile');
  });

  it('reads ProblemDetails, which is what a model-binding failure returns', () => {
    expect(httpErrorMessage(response(400, { title: 'One or more validation errors occurred.' })))
      .toBe('One or more validation errors occurred.');
  });

  it('accepts a plain string body', () => {
    expect(httpErrorMessage(response(500, 'boom'))).toBe('boom');
  });

  it('never surfaces the transport sentence', () => {
    const message = httpErrorMessage(response(400, null));
    expect(message).not.toContain('Http failure');
  });

  it('passes a plain Error through', () => {
    expect(httpErrorMessage(new Error('boom'))).toBe('boom');
    expect(httpErrorMessage(null)).toBe('Errore sconosciuto.');
  });
});
