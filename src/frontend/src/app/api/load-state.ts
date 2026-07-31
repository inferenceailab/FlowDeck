import { HttpErrorResponse } from '@angular/common/http';

/**
 * What a view knows about an in-flight or finished fetch.
 *
 * A discriminated union rather than the usual `data | loading | error` trio of
 * independent fields. Those three permit states that cannot happen — loading
 * *and* errored, data *and* loading — and a template ends up guarding against
 * combinations nobody intended. Here the compiler enforces exactly one.
 */
export type LoadState<T> =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly data: T }
  | { readonly kind: 'error'; readonly message: string };

export const loading = <T>(): LoadState<T> => ({ kind: 'loading' });

export const ready = <T>(data: T): LoadState<T> => ({ kind: 'ready', data });

export const failed = <T>(message: string): LoadState<T> => ({ kind: 'error', message });

/**
 * Turns an HTTP failure into something worth showing an operator.
 *
 * The API returns RFC 9457 problem details, whose `detail` names the definition,
 * instance or types involved — far more use than "an error occurred". A status
 * code alone tells an operator nothing they can act on.
 *
 * Falls back to the status when there is no problem body, and to a plain
 * message when the request never reached the server at all — status 0 means the
 * network failed, not that the API said anything.
 */
export function describeHttpError(error: unknown): string {
  if (!(error instanceof HttpErrorResponse)) {
    return 'Something went wrong.';
  }

  if (error.status === 0) {
    return 'Could not reach the FlowDeck API.';
  }

  const detail = (error.error as { detail?: unknown } | null)?.detail;

  if (typeof detail === 'string' && detail.length > 0) {
    return detail;
  }

  return `The API returned ${error.status} ${error.statusText}.`;
}
