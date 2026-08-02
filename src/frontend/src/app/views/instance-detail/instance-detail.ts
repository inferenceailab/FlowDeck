import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { forkJoin } from 'rxjs';
import { InstanceService } from '../../api/instance.service';
import { LoadState, describeHttpError, failed, loading, ready } from '../../api/load-state';
import { Instance, StepHistoryEntry, WorkflowDefinitionDetail, isTerminal } from '../../api/models';
import { WorkflowService } from '../../api/workflow.service';
import { StatusBadge } from '../../components/status-badge/status-badge';
import { WorkflowShape } from '../../components/workflow-shape/workflow-shape';
import { ROLLBACK_PREFIX, runMarks } from './run-marks';

/** An instance together with its execution history. */
interface InstanceDetailData {
  readonly instance: Instance;
  readonly history: readonly StepHistoryEntry[];
}

/**
 * One instance, with the timeline of what actually ran.
 *
 * The view an operator opens when something went wrong, so its job is to answer
 * *where* and *why* - not merely to report that a run failed.
 */
@Component({
  selector: 'app-instance-detail',
  imports: [DatePipe, DecimalPipe, StatusBadge, WorkflowShape],
  templateUrl: './instance-detail.html',
  styleUrl: './instance-detail.css',
})
export class InstanceDetail implements OnInit {
  private readonly instances = inject(InstanceService);
  private readonly workflows = inject(WorkflowService);

  /** Bound from the route parameter. */
  readonly instanceId = input.required<string>();

  protected readonly state = signal<LoadState<InstanceDetailData>>(loading());

  protected readonly instance = computed(() => {
    const state = this.state();

    return state.kind === 'ready' ? state.data.instance : null;
  });

  protected readonly history = computed(() => {
    const state = this.state();

    return state.kind === 'ready' ? state.data.history : [];
  });

  protected readonly errorMessage = computed(() => {
    const state = this.state();

    return state.kind === 'error' ? state.message : '';
  });

  /**
   * The shape the instance is running, once it has arrived.
   *
   * A second round trip, chained off the instance: the definition id and
   * version are not in the route, so nothing can ask for the shape until the
   * instance says which one it is running.
   */
  protected readonly shape = signal<WorkflowDefinitionDetail | null>(null);

  /**
   * Whether the shape could not be fetched.
   *
   * Held apart from {@link state}, because the shape is supplementary and the
   * timeline is what an operator opened this view for. Folding this into the
   * view's load state would answer "which step failed?" with "could not load
   * this instance", which would be both unhelpful and untrue.
   */
  protected readonly shapeUnavailable = signal(false);

  /** What the run did to each step of the shape, or null with no shape. */
  protected readonly marks = computed(() => {
    const shape = this.shape();
    const instance = this.instance();

    return shape && instance ? runMarks(shape.steps, this.history(), instance) : null;
  });

  /** Whether the confirmation prompt is showing. */
  protected readonly confirming = signal(false);

  /** Whether a cancel request is in flight. */
  protected readonly cancelling = signal(false);

  /** Whether a resume request is in flight. */
  protected readonly resuming = signal(false);

  /** A failed resume, shown without discarding the loaded instance. */
  protected readonly resumeError = signal<string | null>(null);

  /**
   * Whether this instance can be resumed.
   *
   * Only Suspended. Unlike cancel, which applies to anything in flight, resume
   * means "continue from where it parked" — and an instance that is Running has
   * not parked. The API is still the authority; disabling a button is a
   * courtesy, and a stale view can still produce a 409.
   */
  protected readonly canResume = computed(() => this.instance()?.status === 'Suspended');

  /** A failed cancel, shown without discarding the loaded instance. */
  protected readonly cancelError = signal<string | null>(null);

  /**
   * Whether this instance can still be cancelled.
   *
   * Mirrors the engine's rule (ADR-0008) so the UI does not offer an action the
   * API will refuse. The API remains the authority - disabling a button is a
   * courtesy, not enforcement, and a stale view can still produce a 409.
   */
  protected readonly canCancel = computed(() => {
    const instance = this.instance();

    return instance !== null && !isTerminal(instance.status);
  });

  ngOnInit(): void {
    this.load();
  }

  /**
   * Cancels the instance after confirmation.
   *
   * Two steps, not one. Cancelling is irreversible - terminal states are final
   * - and a misclick that silently stops a long-running workflow is expensive.
   */
  protected confirmCancel(): void {
    this.cancelError.set(null);
    this.confirming.set(true);
  }

  protected dismissCancel(): void {
    this.confirming.set(false);
  }

  protected cancel(): void {
    this.cancelling.set(true);
    this.cancelError.set(null);

    this.instances.cancel(this.instanceId()).subscribe({
      next: () => {
        this.cancelling.set(false);
        this.confirming.set(false);

        // Reloaded rather than patched from the response. Cancelling ends the
        // instance, and the history is what an operator looks at next.
        this.load();
      },
      error: (error: unknown) => {
        this.cancelling.set(false);
        this.confirming.set(false);

        // Kept beside the instance rather than replacing the view. A failed
        // cancel does not mean the instance could not be loaded, and blanking
        // the page would lose the context the operator was acting on.
        this.cancelError.set(describeHttpError(error));
      },
    });
  }

  /**
   * Resumes the instance.
   *
   * No confirmation gate, unlike cancel. Resuming is what the workflow was
   * waiting for, and it is not irreversible — a misclick continues something
   * that was going to continue anyway, where a misclick on cancel ends it.
   */
  protected resume(): void {
    this.resuming.set(true);
    this.resumeError.set(null);

    this.instances.resume(this.instanceId()).subscribe({
      next: () => {
        this.resuming.set(false);

        // Reloaded rather than patched from the response: resuming may have
        // run several steps, and the timeline is what an operator looks at
        // next.
        this.load();
      },
      error: (error: unknown) => {
        this.resuming.set(false);
        this.resumeError.set(describeHttpError(error));
      },
    });
  }

  protected load(): void {
    this.state.set(loading());
    this.shape.set(null);
    this.shapeUnavailable.set(false);

    // Both in one go. Fetching sequentially would show the instance and then
    // pop the timeline in a moment later, which reads as a second load rather
    // than one view arriving.
    forkJoin({
      instance: this.instances.get(this.instanceId()),
      history: this.instances.history(this.instanceId()),
    }).subscribe({
      next: (data) => {
        this.state.set(ready(data));
        this.loadShape(data.instance);
      },
      error: (error: unknown) => this.state.set(failed(describeHttpError(error))),
    });
  }

  /**
   * Fetches the shape this run is running, at the version it started on.
   *
   * Pinned to `definitionVersion` rather than taking the latest. A run belongs
   * to the version it began under, and drawing it against a newer shape would
   * put its history on steps it never had - or silently drop steps it did.
   *
   * A failure costs the shape and nothing else. An in-flight instance whose
   * version has since left the registry is exactly the case an operator most
   * needs the timeline for.
   */
  private loadShape(instance: Instance): void {
    this.workflows.get(instance.definitionId, Number(instance.definitionVersion)).subscribe({
      next: (definition) => this.shape.set(definition),
      error: () => this.shapeUnavailable.set(true),
    });
  }

  /**
   * Whether an entry is the point at which the run failed.
   *
   * A step can appear more than once - a re-entered step after a resume - so
   * this matches on the entry's own status rather than on the instance's
   * failed step name, which would mark every attempt.
   */
  protected isFailurePoint(entry: StepHistoryEntry): boolean {
    return entry.status === 'Failed';
  }

  /**
   * Whether this entry is a retry rather than a first execution.
   *
   * `attempt` is generated as `number | string` because the served OpenAPI
   * document declares int32 as either. Coerced here rather than compared
   * loosely in the template, where `'2' > 1` would be a string comparison
   * quietly doing the right thing for the wrong reason.
   */
  protected isRetry(entry: StepHistoryEntry): boolean {
    return Number(entry.attempt) > 1;
  }

  /**
   * Whether this entry is a compensating action rather than a forward step.
   *
   * The engine names them `compensate:<step>` (ADR-0021). Without telling them
   * apart, the timeline shows a step the author never declared and the run
   * reads as having executed something not in the definition.
   */
  protected isRollback(entry: StepHistoryEntry): boolean {
    return entry.stepName.startsWith(ROLLBACK_PREFIX);
  }

  /**
   * The step name an operator reads.
   *
   * The prefix is a wire detail: a rollback row already says it is one, so
   * repeating it in the name is noise.
   */
  protected displayName(entry: StepHistoryEntry): string {
    return this.isRollback(entry) ? entry.stepName.slice(ROLLBACK_PREFIX.length) : entry.stepName;
  }

  /**
   * The compensating actions that failed, for a partial rollback.
   *
   * `CompensationFailed` is the one status that always needs a human, and what
   * they need is which undo did not happen — the engine cannot say how partly
   * an instance was rolled back, only history can.
   */
  protected readonly failedRollbacks = computed(() =>
    this.history().filter((entry) => this.isRollback(entry) && entry.status === 'Failed'),
  );
}
