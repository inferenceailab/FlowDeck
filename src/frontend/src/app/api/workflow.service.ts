import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { WorkflowDefinitionDetail } from './models';

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
   * Describes one definition: the steps it declares and the branches leaving
   * them.
   *
   * No version parameter. The API defaults to the latest registered, which is
   * the shape an operator means when they ask what a workflow does; reading an
   * older version is for an in-flight instance, and nothing in the dashboard
   * asks for that yet.
   */
  get(definitionId: string): Observable<WorkflowDefinitionDetail> {
    return this.http.get<WorkflowDefinitionDetail>(`/api/workflows/${definitionId}`);
  }
}
