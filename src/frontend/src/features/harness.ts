import { provideHttpClient, withFetch } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Type } from '@angular/core';
import { provideRouter } from '@angular/router';

/**
 * Rendering a view inside one scenario step.
 *
 * ## Why everything happens in the When
 *
 * `vitest-cucumber` runs **each step as its own Vitest test**, and Angular
 * resets `TestBed` between tests. A `ComponentFixture` created in a Given is
 * already torn down by the time the When runs — calling `detectChanges()` on it
 * renders into nothing, and the Then sees an empty DOM while every step
 * reports green.
 *
 * That was measured, not assumed: a probe rendering in one step and asserting
 * in the next fails, and `teardown: { destroyAfterEach: false }` does not
 * change it.
 *
 * So the shape of every step file here is:
 *
 * - **Given** records what the world contains, as plain values.
 * - **When** builds the component, renders it, answers its requests, performs
 *   the interaction, and captures the resulting DOM.
 * - **Then** asserts against what the When captured.
 *
 * Plain values and detached DOM nodes survive the reset; Angular objects do
 * not. This reads slightly oddly for a Given like "a suspended instance is
 * displayed", which records rather than displays — but the alternative is a
 * scenario that passes while asserting on nothing.
 */
export interface Rendered {
  /** The rendered DOM, still queryable after the TestBed is reset. */
  readonly element: HTMLElement;

  /** Requests the view made, for scenarios that assert on them. */
  readonly requests: readonly { method: string; url: string }[];
}

/** What a scenario wants the API to do when the view asks. */
export interface Responder {
  /** Answers one request. Return null to leave it pending. */
  respond(request: { method: string; url: string }): { body: object; status?: number } | null;
}

/**
 * Builds a component, renders it, and answers whatever it fetches.
 */
export async function renderView<T>(
  component: Type<T>,
  options: {
    readonly inputs?: Record<string, unknown>;
    readonly responder: Responder;

    /** Runs after the first render, for scenarios that interact. */
    readonly interact?: (fixture: ComponentFixture<T>, flush: () => void) => void | Promise<void>;
  },
): Promise<Rendered> {
  // Reset first, so a step can render more than once. "Given a definition with
  // a fork and a definition with a choice" has to build both and compare them,
  // and TestBed refuses to be configured twice once instantiated. A no-op on
  // the first render of a step, which is every other scenario here.
  TestBed.resetTestingModule();

  await TestBed.configureTestingModule({
    imports: [component],
    providers: [provideHttpClient(withFetch()), provideHttpClientTesting(), provideRouter([])],
  }).compileComponents();

  const fixture = TestBed.createComponent(component);

  for (const [name, value] of Object.entries(options.inputs ?? {})) {
    fixture.componentRef.setInput(name, value);
  }

  const http = TestBed.inject(HttpTestingController);
  const requests: { method: string; url: string }[] = [];

  const flush = (): void => {
    for (const pending of http.match(() => true)) {
      const description = { method: pending.request.method, url: pending.request.url };
      requests.push(description);

      const answer = options.responder.respond(description);

      if (answer === null) {
        // Deliberately left pending: that is what "the request has not
        // resolved" means, and flushing it would make the loading scenario
        // assert against a view that had already finished loading.
        continue;
      }

      if ((answer.status ?? 200) >= 400) {
        pending.flush(answer.body, { status: answer.status!, statusText: 'Error' });
      } else {
        pending.flush(answer.body);
      }
    }

    fixture.detectChanges();
  };

  fixture.detectChanges();
  flush();

  await options.interact?.(fixture, flush);

  return { element: fixture.nativeElement, requests };
}

/** Answers every request with one body. */
export const alwaysRespond = (body: object, status?: number): Responder => ({
  respond: () => ({ body, status }),
});

/** Leaves every request pending. */
export const neverRespond: Responder = { respond: () => null };
