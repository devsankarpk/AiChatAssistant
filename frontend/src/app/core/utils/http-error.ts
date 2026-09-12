import { HttpErrorResponse } from '@angular/common/http';

import { ApiErrorResponse } from '../models/auth.models';

/** Pulls a human-readable message out of the `.NET` `{ error: { code, message } }` shape. */
export function extractErrorMessage(err: HttpErrorResponse): string {
  const body = err.error as ApiErrorResponse | undefined;
  if (body?.error?.message) {
    return body.error.message;
  }
  return err.status === 0
    ? 'Could not reach the server. Is the API running?'
    : 'Something went wrong. Please try again.';
}
