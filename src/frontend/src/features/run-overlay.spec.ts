import { describeFeature, loadFeature } from '@amiceli/vitest-cucumber';
import { expect } from 'vitest';
import { Instance, StepHistoryEntry, WorkflowDefinitionDetail } from '../app/api/models';
import { InstanceDetail } from '../app/views/instance-detail/instance-detail';
import { Rendered, Responder, renderView } from './harness';
import { aBranch, aDefinition, aStep, aWorkflowStep, anInstance, statusOf } from './support';

const feature = await loadFeature('src/features/run-overlay.feature');

const ID = '3f2a0000-0000-0000-0000-000000000001';

/**
 * Answers the three requests the detail view makes: the instance, its history,
 * and the shape of the definition it is running.
 *
 * The shape may be refused, which is a case of its own — an in-flight instance
 * on a version since dropped from the registry.
 */
const overlayResponder = (
  instance: Instance,
  history: StepHistoryEntry[],
  definition: WorkflowDefinitionDetail | number,
): Responder => ({
  respond: (request) => {
    if (request.url.includes('/api/workflows/')) {
      return typeof definition === 'number'
        ? { body: { title: 'Not Found' }, status: definition }
        : { body: definition };
    }

    return { body: request.url.endsWith('/history') ? history : instance };
  },
});

/**
 * Renders the view and flushes twice.
 *
 * The shape is a second round trip, chained off the instance: the definition id
 * and version are not in the route, so nothing can ask for the shape until the
 * instance has arrived. One flush answers the instance and its history; the
 * request the response provokes is still pending when it returns.
 */
const open = (
  instance: Instance,
  history: StepHistoryEntry[],
  definition: WorkflowDefinitionDetail | number,
): Promise<Rendered> =>
  renderView(InstanceDetail, {
    inputs: { instanceId: ID },
    responder: overlayResponder(instance, history, definition),
    interact: (_, flush) => flush(),
  });

const textOf = (element: Element | null): string => element?.textContent?.trim() ?? '';

const shapeStep = (view: Rendered, name: string): HTMLElement =>
  Array.from(view.element.querySelectorAll<HTMLElement>('li.shape-step')).find(
    (step) => textOf(step.querySelector('.step-name')) === name,
  )!;

/** The word a step carries on the shape, or '' where it carries none. */
const markOf = (view: Rendered, name: string): string =>
  textOf(shapeStep(view, name).querySelector('.step-mark'));

const branchNamed = (view: Rendered, name: string): HTMLElement =>
  Array.from(view.element.querySelectorAll<HTMLElement>('li.branch')).find(
    (branch) => textOf(branch.querySelector('.branch-name')) === name,
  )!;

describeFeature(feature, ({ Scenario }) => {
  Scenario('Steps that ran are marked on the shape', ({ Given, When, Then, And }) => {
    let instance: Instance;
    let history: StepHistoryEntry[];
    let definition: WorkflowDefinitionDetail;
    let view: Rendered;

    Given('an instance whose first two steps succeeded', () => {
      definition = aDefinition(
        aWorkflowStep('reserve'),
        aWorkflowStep('charge'),
        aWorkflowStep('ship'),
      );

      instance = anInstance({
        id: ID,
        status: statusOf('Running'),
        currentStepName: 'ship',
        completedAt: null,
      });

      history = [aStep(1, 'reserve'), aStep(2, 'charge')];
    });

    When('I open its detail view', async () => {
      view = await open(instance, history, definition);
    });

    Then('those two steps are marked as run on the shape', () => {
      expect(markOf(view, 'reserve')).toBe('ran');
      expect(markOf(view, 'charge')).toBe('ran');

      // A word, not a colour. jsdom has no layout engine and cannot check
      // contrast, so a state distinguished only by a hue is a state this suite
      // cannot verify and some operators cannot see (ADR-0016).
      expect(shapeStep(view, 'reserve').querySelector('.step-mark')).not.toBeNull();
    });

    And('the step that has not run is not marked as run', () => {
      expect(markOf(view, 'ship')).not.toBe('ran');
    });
  });

  Scenario('The failed step is marked where it happened', ({ Given, When, Then, And }) => {
    let instance: Instance;
    let history: StepHistoryEntry[];
    let definition: WorkflowDefinitionDetail;
    let view: Rendered;

    Given('an instance that failed at a step inside a branch', () => {
      definition = aDefinition(
        aWorkflowStep('check-stock', {
          branches: [
            aBranch('in-stock', [aWorkflowStep('charge')]),
            aBranch('backorder', [aWorkflowStep('notify')]),
          ],
        }),
      );

      instance = anInstance({
        id: ID,
        status: statusOf('Failed'),
        failedStepName: 'charge',
        errorType: 'InvalidOperationException',
        errorMessage: 'card declined',
      });

      history = [aStep(1, 'check-stock'), aStep(2, 'charge', { status: 'Failed' })];
    });

    When('I open its detail view', async () => {
      view = await open(instance, history, definition);
    });

    Then('that step is marked as failed on the shape', () => {
      expect(markOf(view, 'charge')).toBe('failed');
    });

    And('the branch containing it is the one shown as taken', () => {
      expect(branchNamed(view, 'in-stock').classList).not.toContain('branch-not-taken');
      expect(branchNamed(view, 'backorder').classList).toContain('branch-not-taken');
    });
  });

  Scenario('A branch a choice did not take is marked as not taken', ({ Given, When, Then, And }) => {
    let instance: Instance;
    let history: StepHistoryEntry[];
    let definition: WorkflowDefinitionDetail;
    let view: Rendered;

    Given('an instance whose choice took "in-stock"', () => {
      definition = aDefinition(
        aWorkflowStep('check-stock', {
          branches: [
            aBranch('in-stock', [aWorkflowStep('charge')]),
            aBranch('backorder', [aWorkflowStep('notify')]),
          ],
        }),

        // Past the join, and never reached. The scenario needs a step that has
        // not run for a reason other than a branch being skipped.
        aWorkflowStep('ship'),
      );

      instance = anInstance({
        id: ID,
        status: statusOf('Running'),
        currentStepName: 'ship',
        completedAt: null,
      });

      history = [aStep(1, 'check-stock'), aStep(2, 'charge')];
    });

    When('I open its detail view', async () => {
      view = await open(instance, history, definition);
    });

    Then('the steps under "backorder" are marked as not taken', () => {
      expect(markOf(view, 'notify')).toBe('not taken');

      // On the branch as well as on its steps. An operator scanning a large
      // workflow reads the arm, not every step inside it.
      expect(branchNamed(view, 'backorder').classList).toContain('branch-not-taken');
    });

    And('they are distinguishable from steps that have simply not run yet', () => {
      // "Not reached yet" and "on a path we skipped" are different facts, and
      // only the second is provable: a choice takes exactly one branch
      // (ADR-0024), so a sibling having history settles it. Nothing licenses
      // saying the same about a step the run has merely not arrived at.
      expect(markOf(view, 'ship')).not.toBe('not taken');
    });
  });

  Scenario('A fork marks every branch, because every branch runs', ({ Given, When, Then }) => {
    let instance: Instance;
    let history: StepHistoryEntry[];
    let definition: WorkflowDefinitionDetail;
    let view: Rendered;

    Given('a forked instance where one branch finished and the other did not', () => {
      definition = aDefinition(
        aWorkflowStep('prepare', {
          branches: [
            aBranch('branch-1', [aWorkflowStep('email')], { isParallel: true }),
            aBranch('branch-2', [aWorkflowStep('invoice')], { isParallel: true }),
          ],
        }),
      );

      instance = anInstance({
        id: ID,
        status: statusOf('Running'),
        currentStepName: 'invoice',
        completedAt: null,
      });

      history = [aStep(1, 'prepare'), aStep(2, 'email')];
    });

    When('I open its detail view', async () => {
      view = await open(instance, history, definition);
    });

    Then('no branch of the fork is marked as not taken', () => {
      // Every arm of a fork runs. Inferring "skipped" from a sibling having
      // history would be exactly wrong here — the sibling running is what a
      // fork does — and would report finished work as never attempted.
      expect(view.element.querySelectorAll('.branch-not-taken')).toHaveLength(0);
      expect(markOf(view, 'invoice')).not.toBe('not taken');
    });
  });

  Scenario('A rolled-back step is distinguishable from a completed one', ({ Given, When, Then }) => {
    let instance: Instance;
    let history: StepHistoryEntry[];
    let definition: WorkflowDefinitionDetail;
    let view: Rendered;

    Given('a compensated instance', () => {
      definition = aDefinition(
        aWorkflowStep('reserve', { hasCompensation: true }),
        aWorkflowStep('charge', { hasCompensation: true }),
        aWorkflowStep('ship'),
      );

      instance = anInstance({
        id: ID,
        status: statusOf('Compensated'),
        failedStepName: 'ship',
        errorType: 'InvalidOperationException',
        errorMessage: 'courier rejected it',
      });

      history = [
        aStep(1, 'reserve'),
        aStep(2, 'charge'),
        aStep(3, 'ship', { status: 'Failed' }),
        aStep(4, 'compensate:charge'),
        aStep(5, 'compensate:reserve'),
      ];
    });

    When('I open its detail view', async () => {
      view = await open(instance, history, definition);
    });

    Then('the rolled-back steps are marked as undone rather than as run', () => {
      expect(markOf(view, 'reserve')).toBe('undone');
      expect(markOf(view, 'charge')).toBe('undone');

      // The failure point keeps its own mark. It was compensated too - a step
      // that exhausted its retries still is (ADR-0021) - but where the run
      // broke is what this view exists to answer, and the timeline below
      // carries the undo.
      expect(markOf(view, 'ship')).toBe('failed');
    });
  });

  Scenario('The shape not loading does not cost the timeline', ({ Given, When, Then, And }) => {
    let instance: Instance;
    let history: StepHistoryEntry[];
    let view: Rendered;

    Given('an instance whose definition version is no longer registered', () => {
      instance = anInstance({
        id: ID,
        status: statusOf('Failed'),
        failedStepName: 'charge',
        errorType: 'InvalidOperationException',
        errorMessage: 'card declined',
      });

      history = [aStep(1, 'reserve'), aStep(2, 'charge', { status: 'Failed' })];
    });

    When('I open its detail view', async () => {
      view = await open(instance, history, 404);
    });

    Then('the timeline and the failure are still shown', () => {
      // The shape is supplementary. The timeline is what an operator opened
      // this view for, and losing it to a failed second request would be a
      // strictly worse view than the one before the overlay existed.
      expect(view.element.querySelectorAll('.timeline-entry')).toHaveLength(2);
      expect(textOf(view.element.querySelector('.failure-summary'))).toContain('charge');
    });

    And('the shape is reported as unavailable rather than blanking the view', () => {
      // Said, not silently omitted. A missing section reads as "this workflow
      // has no shape", which is never true.
      expect(view.element.querySelector('.shape-unavailable')).not.toBeNull();
      expect(view.element.querySelector('ol.shape')).toBeNull();
    });
  });
});
