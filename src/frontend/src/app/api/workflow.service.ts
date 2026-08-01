import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { WorkflowDefinition, WorkflowDefinitionDetail } from './models';

/**
 * Reads registered workflow definitions.
 *
 * Separate from {@link InstanceService} because it answers a different
 * question: what a workflow *is*, not what one run of it did. Components
 * render; services fetch (ADR-0018).
 */
@Injectable({ providedIn: 'root' })
export class WorkflowService {
  private readonly http = inject(HttpClient);

  /**
   * Every registered definition, with how many instances still run each.
   *
   * The live count is what an operator reads before retiring a version, and it
   * counts the same thing the engine refuses on - a screen disagreeing with
   * what the engine allows would be worse than no screen.
   */
  list(): Observable<WorkflowDefinition[]> {
    return this.http.get<WorkflowDefinition[]>('/api/workflows');
  }

  /**
   * Describes one definition: the steps it declares and the branches leaving
   * them.
   *
   * @param version The version to describe, or omitted for the latest
   * registered. Omitting it is what an operator means when they ask what a
   * workflow does; a *run* is pinned to the version it started on, and drawing
   * it against a newer shape would put its history on steps it never had (#181).
   */
  get(definitionId: string, version?: number): Observable<WorkflowDefinitionDetail> {
    const params = version === undefined ? undefined : new HttpParams().set('version', version);

    return this.http.get<WorkflowDefinitionDetail>(`/api/workflows/${definitionId}`, { params });
  }
}
