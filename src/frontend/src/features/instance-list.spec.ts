import { describeFeature, loadFeature } from '@amiceli/vitest-cucumber';
import { expect } from 'vitest';
import { InstancePage } from '../app/api/models';
import { InstanceList } from '../app/views/instance-list/instance-list';
import { Rendered, alwaysRespond, neverRespond, renderView } from './harness';
import { aPageOf, statusOf } from './support';

const feature = await loadFeature('src/features/instance-list.feature');

const rowsOf = (view: Rendered): HTMLElement[] =>
  Array.from(view.element.querySelectorAll('tbody tr'));

describeFeature(feature, ({ Scenario }) => {
  Scenario('Instances are listed with status', ({ Given, When, Then, And }) => {
    let page: InstancePage;
    let view: Rendered;

    Given('the API returns three instances', () => {
      page = aPageOf(
        { definitionId: 'order-fulfilment', status: statusOf('Running'), currentStepName: 'charge' },
        { definitionId: 'refund', status: statusOf('Completed') },
        { definitionId: 'onboarding', status: statusOf('Suspended'), currentStepName: 'approve' },
      );
    });

    When('I open the instances view', async () => {
      view = await renderView(InstanceList, { responder: alwaysRespond(page) });
    });

    Then('three rows are rendered', () => {
      expect(rowsOf(view).length).toBe(3);
    });

    And('each row shows id, definition, status and start time', () => {
      for (const [index, row] of rowsOf(view).entries()) {
        const text = row.textContent ?? '';
        const instance = page.items[index];

        // The shortened id an operator actually sees, not the full GUID.
        expect(text).toContain(instance.id.slice(0, 8));
        expect(text).toContain(instance.definitionId);
        expect(row.querySelector('app-status-badge')?.textContent).toContain(instance.status);

        // A machine-readable timestamp, so the cell is not merely non-empty.
        expect(row.querySelector('time')?.getAttribute('datetime')).toBe(instance.createdAt);
      }
    });
  });

  Scenario('Failed instances are visually distinct', ({ Given, When, Then }) => {
    let page: InstancePage;
    let view: Rendered;

    Given('an instance with status Failed', () => {
      page = aPageOf({ status: statusOf('Failed') }, { status: statusOf('Completed') });
    });

    When('I open the instances view', async () => {
      view = await renderView(InstanceList, { responder: alwaysRespond(page) });
    });

    Then('that row carries the failure styling', () => {
      const rows = rowsOf(view);

      expect(rows[0].classList).toContain('row-failed');

      // Only that row. A rule applied to every row is no distinction at all,
      // which a single-row fixture would not have caught.
      expect(rows[1].classList).not.toContain('row-failed');
    });
  });

  Scenario('Loading state is shown while fetching', ({ Given, When, Then }) => {
    let view: Rendered;
    let pending = false;

    Given('the instances request has not resolved', () => {
      pending = true;
    });

    When('I open the instances view', async () => {
      // Left unanswered on purpose. Flushing it would render the loaded view
      // and the assertion below would be about a state that had already gone.
      view = await renderView(InstanceList, { responder: pending ? neverRespond : alwaysRespond(aPageOf()) });
    });

    Then('a loading indicator is visible', () => {
      const loading = view.element.querySelector('.state-loading');

      expect(loading).not.toBeNull();

      // Announced, not merely drawn. A sighted user sees a spinner replaced by
      // a table; without a live region a screen reader user gets nothing.
      expect(view.element.querySelector('[aria-live="polite"]')?.getAttribute('aria-busy')).toBe(
        'true',
      );
    });
  });

  Scenario('Empty state is shown when there are no instances', ({ Given, When, Then }) => {
    let page: InstancePage;
    let view: Rendered;

    Given('the API returns an empty list', () => {
      page = aPageOf();
    });

    When('I open the instances view', async () => {
      view = await renderView(InstanceList, { responder: alwaysRespond(page) });
    });

    Then('an empty state message is shown instead of an empty table', () => {
      expect(view.element.querySelector('.state-empty')).not.toBeNull();

      // "Instead of": a table with headers and no rows reads as broken, so the
      // table must be absent rather than merely accompanied by a message.
      expect(view.element.querySelector('table')).toBeNull();
    });
  });

  Scenario('Error state is shown when the API fails', ({ Given, When, Then }) => {
    let view: Rendered;

    Given('the API returns 500', () => {
      // Recorded by the When, which is where the response is supplied.
    });

    When('I open the instances view', async () => {
      view = await renderView(InstanceList, {
        responder: alwaysRespond({ title: 'Server error', detail: 'the store is unreachable' }, 500),
      });
    });

    Then('an error message with a retry action is shown', () => {
      const error = view.element.querySelector('.state-error');

      expect(error).not.toBeNull();

      // role="alert", so a failure interrupts rather than waiting politely
      // behind whatever else is being announced.
      expect(error?.getAttribute('role')).toBe('alert');
      expect(error?.querySelector('button')?.textContent).toContain('Try again');
    });
  });

  Scenario('Status changes appear without a page reload', ({ Given, When, Then }) => {
    let view: Rendered;

    Given('the instances view is open', () => {
      // The When renders it and drives the refresh, for the reason in
      // harness.ts: a fixture cannot survive from one step to the next.
    });

    When('an instance transitions from Running to Completed', async () => {
      let served = 0;

      // Installed *before* the component is created. The view registers its
      // poll with setInterval on init, so switching to fake timers afterwards
      // leaves that interval on the real clock and advancing does nothing -
      // which is exactly how this scenario first passed its When and then
      // asserted "Running".
      vi.useFakeTimers();

      try {
        view = await renderView(InstanceList, {
          responder: {
            respond: () => ({
              body: aPageOf({ status: statusOf(++served === 1 ? 'Running' : 'Completed') }),
            }),
          },
          interact: (fixture, flush) => {
            expect(fixture.nativeElement.textContent).toContain('Running');

            // Advance past the poll interval rather than waiting for it. A
            // test that slept five seconds for this would be slow and flaky.
            vi.advanceTimersByTime(InstanceList.RefreshIntervalMs);
            flush();
          },
        });
      } finally {
        vi.useRealTimers();
      }
    });

    Then('the displayed status updates within the refresh interval', () => {
      const badge = view.element.querySelector('app-status-badge');

      expect(badge?.textContent).toContain('Completed');

      // No reload: the row is still there, replaced in place rather than the
      // table being swapped for a spinner on every tick.
      expect(rowsOf(view).length).toBe(1);
      expect(view.element.querySelector('.state-loading')).toBeNull();
    });
  });
});
