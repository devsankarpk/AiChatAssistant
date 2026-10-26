import { HttpErrorResponse } from '@angular/common/http';

import { extractErrorMessage } from './http-error';

describe('extractErrorMessage', () => {
  it('uses the message from the API error shape', () => {
    const err = new HttpErrorResponse({ status: 401, error: { error: { code: 'unauthorized', message: 'Invalid email or password.' } } });

    expect(extractErrorMessage(err)).toBe('Invalid email or password.');
  });

  it('uses the API message whatever the status code', () => {
    for (const status of [400, 404, 409, 503]) {
      const err = new HttpErrorResponse({ status, error: { error: { code: 'x', message: `message for ${status}` } } });
      expect(extractErrorMessage(err)).toBe(`message for ${status}`);
    }
  });

  it('explains an unreachable server when there is no status (network error)', () => {
    const err = new HttpErrorResponse({ status: 0, error: new ProgressEvent('error') });

    expect(extractErrorMessage(err)).toBe('Could not reach the server. Is the API running?');
  });

  it('falls back to a generic message for a plain-text body', () => {
    const err = new HttpErrorResponse({ status: 500, error: 'Internal Server Error' });

    expect(extractErrorMessage(err)).toBe('Something went wrong. Please try again.');
  });

  it('falls back to a generic message for an empty or null body', () => {
    expect(extractErrorMessage(new HttpErrorResponse({ status: 500 }))).toBe('Something went wrong. Please try again.');
    expect(extractErrorMessage(new HttpErrorResponse({ status: 502, error: null }))).toBe('Something went wrong. Please try again.');
  });

  it('falls back when the JSON has the wrong shape or an empty message', () => {
    expect(extractErrorMessage(new HttpErrorResponse({ status: 400, error: { title: 'Bad Request' } }))).toBe('Something went wrong. Please try again.');
    expect(extractErrorMessage(new HttpErrorResponse({ status: 400, error: { error: { code: 'x', message: '' } } }))).toBe('Something went wrong. Please try again.');
  });
});
