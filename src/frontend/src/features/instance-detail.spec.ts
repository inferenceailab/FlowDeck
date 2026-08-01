import { describeFeature, loadFeature } from '@amiceli/vitest-cucumber';
import { expect } from 'vitest';
import { Instance, StepHistoryEntry } from '../app/api/models';
import { InstanceDetail } from '../app/views/instance-detail/instance-detail';
import { Rendered, Responder, renderView } from './harness';
import { aStep, anInstance, statusOf } from './support';

const feature = await loadFeature('src/features/instance-detail.feature');

const ID = '3f2a0000-0000-0000-0000-000000000001';

/** Answers the detail view's two requests: the instance, then its history. */
const detailResponder = (instance: Instance, history: StepHistoryEntry[]): Responder => ({
  respond: (request) => ({ body: request.url.endsWith('/history') ? history : instance }),
});

const entriesOf = (view: Rendered): HTMLElement[] =>
  Array.from(view.element.querySelectorAll('.timeline-entry'));

describeFeature(feature, ({ Scenario }) => {
  Scenario('Timeline reflects execution history', ({ Given, When, Then }) => {
    let instance: Instance;
    let history: StepHistoryEntry[];
    let view: Rendered;

    Given('an instance that executed steps A, B and C', () => {
      instance = anInstance({ id: ID, status: statusOf('Completed') });
      history = [aStep(1, 'A'), aStep(2, 'B'), aStep(3, 'C')];
    });

    When('I open its detail view', async () => {
      view = await renderView(InstanceDetail, {
        inputs: { instanceId: ID },
        responder: detailResponder(instance, history),
      });
    });

    Then('the timeline shows A, B and C in order with their outcomes', () => {
      const entries = entriesOf(view);

      expect(
        entries.map((entry) => entry.querySelector('.entry-step')?.textContent?.trim()),
      ).toEqual(['A', 'B', 'C']);

      // The outcome word, so the marker glyph is never the only signal.
      expect(
        entries.map((entry) => entry.querySelector('.entry-status')?.textContent?.trim()),
      ).toEqual(['Success', 'Success', 'Success']);

      // An ordered list: the sequence is the meaning, and a bare div stack
      // would not convey it to a screen reader.
      expect(view.element.querySelector('ol.timeline')).not.toBeNull();
    });
  });

  Scenario('The failing step is called out', ({ Given, When, Then, And }) => {
    let instance: Instance;
    let history: StepHistoryEntry[];
    let view: Rendered;

    Given('an instance that failed at step B', () => {
      instance = anInstance({
        id: ID,
        status: statusOf('Failed'),
        failedStepName: 'B',
        errorType: 'InvalidOperationException',
        errorMessage: 'card declined',
      });

      history = [
        aStep(1, 'A'),
        aStep(2, 'B', {
          status: 'Failed',
          errorType: 'InvalidOperationException',
          errorMessage: 'card declined',
        }),
      ];
    });

    When('I open its detail view', async () => {
      view = await renderView(InstanceDetail, {
        inputs: { instanceId: ID },
        responder: detailResponder(instance, history),
      });
    });

    Then('step B is marked as the failure point', () => {
      const entries = entriesOf(view);

      expect(entries[1].classList).toContain('entry-failed');

      // Only B. A step can appear more than once, so marking on the instance's
      // failed step name rather than the entry's own status would mark every
      // attempt at it.
      expect(entries[0].classList).not.toContain('entry-failed');
    });

    And('the recorded error message is shown', () => {
      const summary = view.element.querySelector('.failure-summary');

      // Stated before the timeline, not buried in it: an operator arriving
      // after an alert wants the answer first and the evidence second.
      expect(summary?.getAttribute('role')).toBe('alert');
      expect(summary?.textContent).toContain('Failed at step B');
      expect(summary?.textContent).toContain('card declined');
    });
  });

  Scenario('Compensating actions appear in the timeline', ({ Given, When, Then }) => {
    let instance: Instance;
    let history: StepHistoryEntry[];
    let view: Rendered;

    Given('an instance that rolled back two steps', () => {
      instance = anInstance({
        id: ID,
        status: statusOf('Compensated'),
        failedStepName: 'ship',
        errorMessage: 'no carrier',
      });

      history = [
        aStep(1, 'reserve'),
        aStep(2, 'charge'),
        aStep(3, 'ship', { status: 'Failed', errorMessage: 'no carrier' }),
        aStep(4, 'compensate:charge'),
        aStep(5, 'compensate:reserve'),
      ];
    });

    When('I open its detail view', async () => {
      view = await renderView(InstanceDetail, {
        inputs: { instanceId: ID },
        responder: detailResponder(instance, history),
      });
    });

    Then('the timeline shows both compensating actions, marked as rollback', () => {
      const entries = entriesOf(view);
      const rollback = entries.filter((entry) => entry.classList.contains('entry-rollback'));

      expect(rollback.length).toBe(2);

      // The compensate: prefix is a wire detail; the row already says it is a
      // rollback, so repeating it in the name would be noise.
      expect(
        rollback.map((entry) => entry.querySelector('.entry-step')?.textContent?.trim()),
      ).toEqual(['charge', 'reserve']);

      // Forward steps are not marked, or the distinction says nothing.
      expect(entries[0].classList).not.toContain('entry-rollback');
    });
  });

  Scenario('A partial rollback is called out', ({ Given, When, Then }) => {
    let instance: Instance;
    let history: StepHistoryEntry[];
    let view: Rendered;

    Given('a CompensationFailed instance', () => {
      instance = anInstance({
        id: ID,
        status: statusOf('CompensationFailed'),
        failedStepName: 'ship',
        errorMessage: 'no carrier',
      });

      history = [
        aStep(1, 'reserve'),
        aStep(2, 'charge'),
        aStep(3, 'ship', { status: 'Failed', errorMessage: 'no carrier' }),
        aStep(4, 'compensate:charge', {
          status: 'Failed',
          errorType: 'TimeoutException',
          errorMessage: 'gateway unreachable',
        }),
        aStep(5, 'compensate:reserve'),
      ];
    });

    When('I open its detail view', async () => {
      view = await renderView(InstanceDetail, {
        inputs: { instanceId: ID },
        responder: detailResponder(instance, history),
      });
    });

    Then('it states which compensating actions failed', () => {
      const summary = view.element.querySelector('.rollback-summary');

      expect(summary).not.toBeNull();
      expect(summary?.getAttribute('role')).toBe('alert');

      // Names the step that could not be undone, and why. "Rollback
      // incomplete" alone would leave an operator to work out which one.
      expect(summary?.textContent).toContain('charge');
      expect(summary?.textContent).toContain('gateway unreachable');

      // Only the failed one. Listing the successful rollback too would send
      // someone to check work that was already undone.
      expect(summary?.textContent).not.toContain('reserve');
    });
  });
});
