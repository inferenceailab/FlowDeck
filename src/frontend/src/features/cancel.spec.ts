import { describeFeature, loadFeature } from '@amiceli/vitest-cucumber';
import { expect } from 'vitest';
import { Instance } from '../app/api/models';
import { InstanceDetail } from '../app/views/instance-detail/instance-detail';
import { Rendered, Responder, renderView } from './harness';
import { anInstance, statusOf } from './support';

const feature = await loadFeature('src/features/cancel.feature');

const ID = '3f2a0000-0000-0000-0000-000000000001';

describeFeature(feature, ({ Scenario }) => {
  Scenario('Cancel action calls the API and refreshes', ({ Given, When, Then, And }) => {
    let view: Rendered;
    let displayed: Instance;

    Given('a suspended instance is displayed', () => {
      displayed = anInstance({ id: ID, status: statusOf('Suspended'), currentStepName: 'approve' });
    });

    When('I trigger the cancel action and confirm', async () => {
      // The instance is Suspended until the cancel succeeds, then Cancelled.
      // Served from a flag rather than a call count, because the view fetches
      // both the instance and its history on each load and counting requests
      // would flip the status on the wrong one.
      let cancelled = false;

      const responder: Responder = {
        respond: (request) => {
          if (request.url.endsWith('/cancel')) {
            cancelled = true;
            return { body: {} };
          }

          return {
            body: request.url.endsWith('/history')
              ? []
              : { ...displayed, status: cancelled ? 'Cancelled' : 'Suspended' },
          };
        },
      };

      view = await renderView(InstanceDetail, {
        inputs: { instanceId: ID },
        responder,
        interact: (fixture, flush) => {
          const element: HTMLElement = fixture.nativeElement;

          // Two deliberate actions, not one. Cancelling is irreversible, and a
          // misclick that silently stops a long-running workflow is expensive
          // - so the first click only opens the confirmation.
          element.querySelector<HTMLButtonElement>('.cancel-button')!.click();
          fixture.detectChanges();

          // The confirmation is an alertdialog with two actions; the first
          // confirms. Selected through the existing structure rather than by
          // adding a class to production markup for a test's convenience.
          const dialog = element.querySelector('[role="alertdialog"]');
          expect(dialog, 'the first click should open a confirmation').not.toBeNull();

          const confirm = dialog!.querySelector<HTMLButtonElement>('.confirm-actions button');
          expect(confirm?.textContent).toContain('Yes, cancel it');

          confirm!.click();

          // Twice: the first flush answers the cancel, which then triggers a
          // reload whose own requests are only issued once that response has
          // been delivered. One flush leaves the view mid-reload, showing
          // nothing.
          flush();
          flush();
        },
      });
    });

    Then('POST to the cancel endpoint is called', () => {
      expect(view.requests).toContainEqual({
        method: 'POST',
        url: `/api/instances/${ID}/cancel`,
      });
    });

    And('the row status updates to Cancelled', () => {
      // Reloaded rather than patched from the cancel response: cancelling ends
      // the instance, and the history is what an operator looks at next.
      expect(view.element.querySelector('app-status-badge')?.textContent).toContain('Cancelled');
    });
  });

  Scenario('Cancel is unavailable for completed instances', ({ Given, Then }) => {
    let view: Rendered;

    Given('a completed instance is displayed', async () => {
      const instance = anInstance({ id: ID, status: statusOf('Completed') });

      // This scenario has no When: the issue wrote it as Given/Then, and the
      // absence of an action is the point. Rendering therefore happens here.
      view = await renderView(InstanceDetail, {
        inputs: { instanceId: ID },
        responder: {
          respond: (request) => ({ body: request.url.endsWith('/history') ? [] : instance }),
        },
      });
    });

    Then('the cancel action is disabled', () => {
      const cancel = view.element.querySelector<HTMLButtonElement>('.cancel-button');

      // Present and disabled, not absent. A button that vanishes leaves an
      // operator wondering whether they misremembered it; one that is disabled
      // says the action exists and does not apply here.
      expect(cancel).not.toBeNull();
      expect(cancel!.disabled).toBe(true);
    });
  });

  Scenario('Cancelling and rolling back is a separate action', ({ Given, When, Then, And }) => {
    let view: Rendered;
    let displayed: Instance;

    // Captured while the prompt is open. Confirming closes it, so reading the
    // wording afterwards would assert against a dialog that is no longer there.
    let promptText = '';

    Given('a suspended instance is displayed', () => {
      displayed = anInstance({ id: ID, status: statusOf('Suspended'), currentStepName: 'approve' });
    });

    When('I trigger the cancel and roll back action', async () => {
      view = await renderView(InstanceDetail, {
        inputs: { instanceId: ID },
        responder: {
          respond: (request) =>
            request.url.includes('/api/workflows/')
              ? { body: { title: 'Not Found' }, status: 404 }
              : { body: request.url.endsWith('/history') ? [] : displayed },
        },
        interact: (fixture, flush) => {
          flush();
          fixture.nativeElement.querySelector('.rollback-button').click();
          fixture.detectChanges();

          promptText = fixture.nativeElement.querySelector('.confirm').textContent ?? '';

          fixture.nativeElement.querySelector('.confirm-actions button').click();
          fixture.detectChanges();
          flush();
        },
      });
    });

    Then('the prompt says work already done will be reversed', () => {
      // Asserted on the rendered prompt rather than on the button label,
      // because the prompt is the last thing an operator reads before an
      // irreversible action.
      expect(promptText).toContain('will be reversed');
    });

    And('confirming calls the cancel-and-roll-back endpoint', () => {
      expect(
        view.requests.filter((request) => request.url.endsWith('/cancel-and-roll-back')),
      ).toHaveLength(1);

      // And not the plain one. Two buttons wired to the same call would pass
      // every other assertion here.
      expect(view.requests.filter((request) => request.url.endsWith('/cancel'))).toHaveLength(0);
    });
  });

  Scenario('Plain cancel says work is not reversed', ({ Given, When, Then }) => {
    let view: Rendered;
    let displayed: Instance;

    Given('a suspended instance is displayed', () => {
      displayed = anInstance({ id: ID, status: statusOf('Suspended'), currentStepName: 'approve' });
    });

    When('I trigger the cancel action', async () => {
      view = await renderView(InstanceDetail, {
        inputs: { instanceId: ID },
        responder: {
          respond: (request) =>
            request.url.includes('/api/workflows/')
              ? { body: { title: 'Not Found' }, status: 404 }
              : { body: request.url.endsWith('/history') ? [] : displayed },
        },
        interact: (fixture, flush) => {
          flush();
          fixture.nativeElement.querySelector('.cancel-button').click();
          fixture.detectChanges();
        },
      });
    });

    Then('the prompt says work already done is not reversed', () => {
      expect(view.element.textContent).toContain('not');
      expect(view.element.textContent).toContain('reversed');
      expect(view.element.textContent).not.toContain('will be reversed');
    });
  });
});
