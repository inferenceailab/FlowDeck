import { describeFeature, loadFeature } from '@amiceli/vitest-cucumber';
import { expect } from 'vitest';
import { Instance } from '../app/api/models';
import { InstanceDetail } from '../app/views/instance-detail/instance-detail';
import { Rendered, Responder, renderView } from './harness';
import { anInstance, statusOf } from './support';

const feature = await loadFeature('src/features/resume.feature');

const ID = '3f2a0000-0000-0000-0000-000000000001';

/**
 * Answers the detail view's requests, refusing the resume when asked to.
 *
 * The shape request is refused throughout: this feature is about the actions,
 * and a shape that failed to load must not affect them (#181).
 */
const responder = (instance: Instance, refuseResume = false): Responder => ({
  respond: (request) => {
    if (request.url.endsWith('/resume')) {
      return refuseResume
        ? { body: { detail: 'Workflow instance cannot move from Completed to Running.' }, status: 409 }
        : { body: instance };
    }

    if (request.url.includes('/api/workflows/')) {
      return { body: { title: 'Not Found' }, status: 404 };
    }

    return { body: request.url.endsWith('/history') ? [] : instance };
  },
});

const open = (instance: Instance, refuseResume = false): Promise<Rendered> =>
  renderView(InstanceDetail, {
    inputs: { instanceId: ID },
    responder: responder(instance, refuseResume),
    interact: (_, flush) => flush(),
  });

const resumeButton = (view: Rendered): HTMLButtonElement =>
  view.element.querySelector('.resume-button')!;

const clickResume = (instance: Instance, refuseResume = false): Promise<Rendered> =>
  renderView(InstanceDetail, {
    inputs: { instanceId: ID },
    responder: responder(instance, refuseResume),
    interact: (fixture, flush) => {
      flush();
      fixture.nativeElement.querySelector('.resume-button').click();
      fixture.detectChanges();
      flush();
      flush();
    },
  });

describeFeature(feature, ({ Scenario }) => {
  Scenario('A suspended instance offers resume', ({ Given, When, Then }) => {
    let instance: Instance;
    let view: Rendered;

    Given('a suspended instance is displayed', () => {
      instance = anInstance({ id: ID, status: statusOf('Suspended'), completedAt: null });
    });

    When('I look at the actions', async () => {
      view = await open(instance);
    });

    Then('resume is offered', () => {
      expect(resumeButton(view)).not.toBeNull();
      expect(resumeButton(view).disabled).toBe(false);
    });
  });

  Scenario('Resume is disabled for an instance that has not parked', ({ Given, When, Then }) => {
    let instance: Instance;
    let view: Rendered;

    Given('a running instance is displayed', () => {
      instance = anInstance({ id: ID, status: statusOf('Running'), completedAt: null });
    });

    When('I look at the actions', async () => {
      view = await open(instance);
    });

    Then('resume is present but disabled', () => {
      // Disabled rather than hidden, the same rule cancel follows: a control
      // that vanishes leaves an operator wondering whether they misremembered
      // it. Resume means "continue from where it parked", and a Running
      // instance has not parked.
      expect(resumeButton(view)).not.toBeNull();
      expect(resumeButton(view).disabled).toBe(true);
    });
  });

  Scenario('Resuming asks the API and reloads', ({ Given, When, Then, And }) => {
    let instance: Instance;
    let view: Rendered;

    Given('a suspended instance is displayed', () => {
      instance = anInstance({ id: ID, status: statusOf('Suspended'), completedAt: null });
    });

    When('I resume it', async () => {
      view = await clickResume(instance);
    });

    Then('the resume endpoint is called', () => {
      expect(view.requests.filter((request) => request.url.endsWith('/resume'))).toHaveLength(1);
    });

    And('the instance is reloaded afterwards', () => {
      // Reloaded rather than patched from the response: resuming may have run
      // several steps, and the timeline is what an operator looks at next.
      const instanceReads = view.requests.filter(
        (request) => request.method === 'GET' && request.url.endsWith(ID),
      );

      expect(instanceReads.length).toBeGreaterThan(1);
    });
  });

  Scenario('A refused resume is reported without losing the instance', ({ Given, When, Then, And }) => {
    let instance: Instance;
    let view: Rendered;

    Given('a suspended instance the API will refuse to resume', () => {
      instance = anInstance({ id: ID, status: statusOf('Suspended'), completedAt: null });
    });

    When('I resume it', async () => {
      view = await clickResume(instance, true);
    });

    Then('the refusal is shown', () => {
      // The usual cause is a 409: somebody else resumed it, or it finished
      // while this page was open. The message the API sent is surfaced rather
      // than replaced with a generic one.
      const error = view.element.querySelector('.resume-error');

      expect(error?.textContent).toContain('Could not resume this instance');
      expect(error?.textContent).toContain('cannot move from Completed to Running');
    });

    And('the instance is still on screen', () => {
      // A failed resume does not mean the instance could not be loaded.
      // Blanking the page would lose the context the operator was acting on.
      expect(view.element.textContent).toContain('order-fulfilment');
    });
  });
});
