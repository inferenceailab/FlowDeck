import { describeFeature, loadFeature } from '@amiceli/vitest-cucumber';
import { expect } from 'vitest';
import { WorkflowDefinition } from '../app/api/models';
import { WorkflowList } from '../app/views/workflow-list/workflow-list';
import { Rendered, alwaysRespond, renderView } from './harness';

const feature = await loadFeature('src/features/workflow-list.feature');

const open = (definitions: WorkflowDefinition[]): Promise<Rendered> =>
  renderView(WorkflowList, { responder: alwaysRespond(definitions) });

const textOf = (element: Element | null): string => element?.textContent?.trim() ?? '';

const rows = (view: Rendered): HTMLElement[] =>
  Array.from(view.element.querySelectorAll('tr.workflow-row'));

/** The usage cell of the row for one version. */
const usageOf = (view: Rendered, version: number): string =>
  textOf(
    rows(view)
      .find((row) => textOf(row.querySelector('td')) === `v${version}`)!
      .querySelector('.workflow-usage'),
  );

describeFeature(feature, ({ Scenario }) => {
  Scenario('Every registered version is listed', ({ Given, When, Then }) => {
    let definitions: WorkflowDefinition[];
    let view: Rendered;

    Given('two versions of "orders" are registered', () => {
      definitions = [
        { id: 'orders', version: 1, inputTypeName: null, activeInstances: 0 },
        { id: 'orders', version: 2, inputTypeName: 'OrderRequest', activeInstances: 0 },
      ];
    });

    When('I open the workflows view', async () => {
      view = await open(definitions);
    });

    Then('both versions are shown with their ids and versions', () => {
      expect(rows(view)).toHaveLength(2);

      expect(rows(view).map((row) => textOf(row.querySelector('th')))).toEqual(['orders', 'orders']);
      expect(rows(view).map((row) => textOf(row.querySelector('td')))).toEqual(['v1', 'v2']);

      // A table, with a caption and row headers. Two versions of one workflow
      // are only distinguishable by the version column, so the structure is
      // what a screen reader needs to read the pair apart.
      expect(view.element.querySelector('table caption')).not.toBeNull();
    });
  });

  Scenario('A version something is running says it cannot be retired', ({ Given, When, Then, And }) => {
    let definitions: WorkflowDefinition[];
    let view: Rendered;

    Given('"orders" v1 has one live instance and v2 has none', () => {
      definitions = [
        { id: 'orders', version: 1, inputTypeName: null, activeInstances: 1 },
        { id: 'orders', version: 2, inputTypeName: null, activeInstances: 0 },
      ];
    });

    When('I open the workflows view', async () => {
      view = await open(definitions);
    });

    Then('v1 says it cannot be retired, and how many are running', () => {
      // The number and the consequence. A count alone leaves an operator to
      // work out what it stops them doing; "cannot be retired" is the answer
      // to the question they actually opened this view with.
      expect(usageOf(view, 1)).toContain('1');
      expect(usageOf(view, 1)).toContain('cannot be retired');
    });

    And('v2 says it is safe to retire', () => {
      expect(usageOf(view, 2)).toContain('safe to retire');

      // In words, not only by a tint. Delete every colour rule and the table
      // still answers the question (ADR-0016).
      expect(usageOf(view, 2)).not.toContain('cannot');
    });
  });

  Scenario('A count of zero is not read as busy', ({ Given, When, Then }) => {
    let definitions: WorkflowDefinition[];
    let view: Rendered;

    Given('a version reporting zero live instances as a string', () => {
      // The generated type is number | string, because the served document
      // declares int32 as either. '0' is truthy, so a template testing it
      // loosely would report every idle version as busy - the exact inversion
      // an operator would act on.
      definitions = [
        { id: 'orders', version: 1, inputTypeName: null, activeInstances: '0' as unknown as number },
      ];
    });

    When('I open the workflows view', async () => {
      view = await open(definitions);
    });

    Then('it is shown as safe to retire', () => {
      expect(usageOf(view, 1)).toContain('safe to retire');
      expect(rows(view)[0].classList).not.toContain('row-in-use');
    });
  });

  Scenario('A host with nothing registered says so', ({ Given, When, Then }) => {
    let view: Rendered;

    Given('no workflows are registered', () => {
      // Nothing to arrange; the empty response is the arrangement.
    });

    When('I open the workflows view', async () => {
      view = await open([]);
    });

    Then('it says nothing is registered rather than showing an empty table', () => {
      expect(textOf(view.element.querySelector('.state-empty'))).toContain('no workflow definitions');

      // A table with headers and no rows reads as a failed load.
      expect(view.element.querySelector('table')).toBeNull();
    });
  });
});
