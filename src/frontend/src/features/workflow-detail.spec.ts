import { describeFeature, loadFeature } from '@amiceli/vitest-cucumber';
import { expect } from 'vitest';
import { WorkflowDefinitionDetail } from '../app/api/models';
import { WorkflowDetail } from '../app/views/workflow-detail/workflow-detail';
import { Rendered, alwaysRespond, renderView } from './harness';
import { DEFINITION_ID, aBranch, aDefinition, aWorkflowStep } from './support';

const feature = await loadFeature('src/features/workflow-detail.feature');

const open = (definition: WorkflowDefinitionDetail): Promise<Rendered> =>
  renderView(WorkflowDetail, {
    inputs: { definitionId: DEFINITION_ID },
    responder: alwaysRespond(definition),
  });

const textOf = (element: Element | null): string => element?.textContent?.trim() ?? '';

/**
 * Step names at the top level of the shape, in rendered order.
 *
 * Anchored to the outermost list rather than to every `.shape-step`, because
 * the branch bodies are `.shape-step`s too and a flat query would return them
 * as though they sat in the top-level sequence. The `app-workflow-shape` in the
 * path is the shared renderer both detail views draw with (#181).
 */
const topLevelStepNames = (view: Rendered): string[] =>
  Array.from(
    view.element.querySelectorAll(':scope > div > app-workflow-shape > ol.shape > li.shape-step'),
  ).map((step) => textOf(step.querySelector('.step-name')));

const stepNamed = (view: Rendered, name: string): HTMLElement =>
  Array.from(view.element.querySelectorAll<HTMLElement>('li.shape-step')).find(
    (step) => textOf(step.querySelector('.step-name')) === name,
  )!;

const branches = (view: Rendered): HTMLElement[] =>
  Array.from(view.element.querySelectorAll('li.branch'));

describeFeature(feature, ({ Scenario }) => {
  Scenario('A linear definition renders as an ordered sequence', ({ Given, When, Then }) => {
    let definition: WorkflowDefinitionDetail;
    let view: Rendered;

    Given('a definition with three sequential steps', () => {
      // Not in alphabetical order, so "in order" is distinguishable from any
      // order a sort would also produce.
      definition = aDefinition(
        aWorkflowStep('reserve'),
        aWorkflowStep('charge'),
        aWorkflowStep('ship'),
      );
    });

    When('I open its detail view', async () => {
      view = await open(definition);
    });

    Then('the three steps are shown in order', () => {
      expect(topLevelStepNames(view)).toEqual(['reserve', 'charge', 'ship']);

      // An ordered list, because the sequence is the meaning. A stack of divs
      // renders identically and tells a screen reader nothing about order.
      expect(view.element.querySelector('ol.shape')).not.toBeNull();
    });
  });

  Scenario('A choice renders its branches', ({ Given, When, Then, And }) => {
    let definition: WorkflowDefinitionDetail;
    let view: Rendered;

    Given('a definition whose step branches into "in-stock" and "backorder"', () => {
      definition = aDefinition(
        aWorkflowStep('check-stock', {
          branches: [
            aBranch('in-stock', [aWorkflowStep('charge')]),
            aBranch('backorder', [aWorkflowStep('notify')]),
          ],
        }),
      );
    });

    When('I open its detail view', async () => {
      view = await open(definition);
    });

    Then('both branches are shown, labelled with their names', () => {
      expect(branches(view).map((branch) => textOf(branch.querySelector('.branch-name')))).toEqual([
        'in-stock',
        'backorder',
      ]);
    });

    And('each branch shows the steps inside it', () => {
      // The bodies, not only the labels. A branch drawn as a name with nothing
      // under it is an edge leading nowhere, which is not what was declared.
      expect(
        branches(view).map((branch) => textOf(branch.querySelector('.shape-step .step-name'))),
      ).toEqual(['charge', 'notify']);

      // Nested inside the branch, so the tree is a tree. Two flat lists would
      // satisfy the assertion above and lose which steps belong to which arm.
      expect(branches(view)[0].querySelector('ol.shape')).not.toBeNull();
    });
  });

  Scenario('A fork is distinguishable from a choice', ({ Given, When, Then, And }) => {
    let fork: WorkflowDefinitionDetail;
    let choice: WorkflowDefinitionDetail;
    let forkView: Rendered;
    let choiceView: Rendered;

    Given('a definition with a fork and a definition with a choice', () => {
      fork = aDefinition(
        aWorkflowStep('prepare', {
          branches: [
            aBranch('branch-1', [aWorkflowStep('email')], { isParallel: true }),
            aBranch('branch-2', [aWorkflowStep('invoice')], { isParallel: true }),
          ],
        }),
      );

      choice = aDefinition(
        aWorkflowStep('check-stock', {
          branches: [
            aBranch('in-stock', [aWorkflowStep('charge')]),
            aBranch('backorder', [aWorkflowStep('notify')]),
          ],
        }),
      );
    });

    When('I open each detail view', async () => {
      forkView = await open(fork);
      choiceView = await open(choice);
    });

    Then('the fork states that every branch runs', () => {
      // Words, not a colour or a glyph. The difference between "all of these
      // happen" and "one of these happens" is the whole meaning of the shape,
      // and it is never safe to leave it to styling (ADR-0016).
      expect(textOf(forkView.element.querySelector('.branch-rule'))).toContain('Every branch runs');
    });

    And('the choice states that one branch is taken', () => {
      expect(textOf(choiceView.element.querySelector('.branch-rule'))).toContain(
        'One branch is taken',
      );

      // Not merely different text - the fork's sentence must not appear here,
      // or a template that printed both would pass.
      expect(choiceView.element.textContent).not.toContain('Every branch runs');
    });
  });

  Scenario('A retrying step shows its policy', ({ Given, When, Then, And }) => {
    let definition: WorkflowDefinitionDetail;
    let view: Rendered;

    Given('a definition whose step allows three attempts', () => {
      definition = aDefinition(
        aWorkflowStep('charge', { maxAttempts: 3 }),
        aWorkflowStep('ship'),
      );
    });

    When('I open its detail view', async () => {
      view = await open(definition);
    });

    Then('that step shows it retries', () => {
      const retry = stepNamed(view, 'charge').querySelector('.step-retry');

      expect(retry).not.toBeNull();

      // The number, not just the fact. "Retries" alone leaves an operator
      // unable to tell a single extra attempt from twenty.
      expect(textOf(retry)).toContain('3');
    });

    And('a step that does not retry says nothing about attempts', () => {
      // Absent rather than "1 attempt". Every step on an ordinary workflow
      // would carry the badge, which is noise on the common case to serve the
      // rare one - the same rule the instance timeline follows.
      expect(stepNamed(view, 'ship').querySelector('.step-retry')).toBeNull();
    });
  });

  Scenario('A compensated step is marked', ({ Given, When, Then, And }) => {
    let definition: WorkflowDefinitionDetail;
    let view: Rendered;

    Given('a definition whose step declares a compensating action', () => {
      definition = aDefinition(
        aWorkflowStep('charge', { hasCompensation: true }),
        aWorkflowStep('ship'),
      );
    });

    When('I open its detail view', async () => {
      view = await open(definition);
    });

    Then('that step is marked as having an undo', () => {
      const undo = stepNamed(view, 'charge').querySelector('.step-undo');

      expect(undo).not.toBeNull();
      expect(textOf(undo)).toContain('undo');
    });

    And('a step with no compensating action is not', () => {
      expect(stepNamed(view, 'ship').querySelector('.step-undo')).toBeNull();
    });
  });
});
