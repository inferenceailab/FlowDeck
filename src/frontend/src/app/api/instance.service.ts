import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { Instance, InstancePage, InstanceStatus, StepHistoryEntry } from './models';

/** Query for a page of instances. */
export interface InstanceQuery {
  readonly status?: InstanceStatus;
  readonly definitionId?: string;
  readonly page?: number;
  readonly pageSize?: number;
}

/**
 * Reads and operates on workflow instances.
 *
 * The only place in the application that knows the API's URL shape. Components
 * render; services fetch (ADR-0018). A component reaching for `HttpClient`
 * directly is the drift that decision named as its own risk.
 */
@Injectable({ providedIn: 'root' })
export class InstanceService {
  private readonly http = inject(HttpClient);

  /** Lists instances, newest first. */
  list(query: InstanceQuery = {}): Observable<InstancePage> {
    let params = new HttpParams();

    // Omitted rather than sent empty. `?status=` would be an unrecognised
    // status value and a 400, not "no filter".
    if (query.status) {
      params = params.set('status', query.status);
    }

    if (query.definitionId) {
      params = params.set('definitionId', query.definitionId);
    }

    if (query.page !== undefined) {
      params = params.set('page', query.page);
    }

    if (query.pageSize !== undefined) {
      params = params.set('pageSize', query.pageSize);
    }

    return this.http.get<InstancePage>('/api/instances', { params });
  }

  /** Retrieves one instance. */
  get(instanceId: string): Observable<Instance> {
    return this.http.get<Instance>(`/api/instances/${instanceId}`);
  }

  /** Reads an instance's execution history, in order. */
  history(instanceId: string): Observable<StepHistoryEntry[]> {
    return this.http.get<StepHistoryEntry[]>(`/api/instances/${instanceId}/history`);
  }

  /** Stops an instance permanently. */
  /**
   * Continues a suspended instance.
   *
   * A 409 comes back if it is no longer suspended — because it finished, or
   * because another operator got there first. The view surfaces that rather
   * than retrying: two resumes running one instance is what NFR-1 forbids.
   */
  resume(instanceId: string): Observable<Instance> {
    return this.http.post<Instance>(`/api/instances/${instanceId}/resume`, null);
  }

  /**
   * Stops an instance and unwinds the work it had completed.
   *
   * A different action from {@link cancel}, not a variant of it: one keeps the
   * work, the other undoes it, and both are irreversible.
   */
  cancelAndRollBack(instanceId: string): Observable<Instance> {
    return this.http.post<Instance>(`/api/instances/${instanceId}/cancel-and-roll-back`, null);
  }

  cancel(instanceId: string): Observable<Instance> {
    return this.http.post<Instance>(`/api/instances/${instanceId}/cancel`, null);
  }
}
