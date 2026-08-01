import { describeFeature, loadFeature } from '@amiceli/vitest-cucumber';
import { provideHttpClient, withFetch } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { expect } from 'vitest';
import { App } from '../app/app';
import { routes } from '../app/app.routes';

const feature = await loadFeature('src/features/shell.feature');

describeFeature(feature, ({ Scenario }) => {
  Scenario('Shell renders with primary navigation', ({ Given, When, Then, And }) => {
    let route = '/';
    let element: HTMLElement;

    Given('the application has loaded', () => {
      route = '/instances';
    });

    When('I view any route', async () => {
      // Built and navigated here rather than in the Given: each step is its own
      // test and Angular resets TestBed between them, so a fixture created
      // earlier would already be torn down. See harness.ts.
      await TestBed.configureTestingModule({
        imports: [App],
        providers: [
          provideRouter(routes),

          // Navigating lazily loads a real view, which fetches on init.
          // Without a testing backend that request escapes to the network and
          // surfaces as an unhandled rejection: a green suite beside a red
          // exit code.
          provideHttpClient(withFetch()),
          provideHttpClientTesting(),
        ],
      }).compileComponents();

      const fixture = TestBed.createComponent(App);

      // A real navigation, not just a render. The active-route assertion
      // depends on the router having resolved something.
      await TestBed.inject(Router).navigate([route]);
      fixture.detectChanges();

      // The lazily-loaded view's request is expected; it must simply not have
      // reached the network.
      TestBed.inject(HttpTestingController).match(() => true);

      element = fixture.nativeElement;
    });

    Then('the header and primary navigation are visible', () => {
      const links: HTMLElement[] = Array.from(element.querySelectorAll('nav a'));

      expect(element.querySelector('header')).not.toBeNull();
      expect(links.map((link) => link.textContent?.trim())).toEqual(['Instances', 'Workflows']);
    });

    And('the active route is highlighted', () => {
      const active: HTMLElement[] = Array.from(element.querySelectorAll('nav a.active'));

      // Exactly one. Every link marked active is the same as none being marked.
      expect(active.length).toBe(1);
      expect(active[0].textContent?.trim()).toBe('Instances');

      // aria-current, not only a class: the highlight has to reach a screen
      // reader, or "highlighted" means highlighted for sighted users only.
      expect(active[0].getAttribute('aria-current')).toBe('page');
    });
  });
});
