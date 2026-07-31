import { provideHttpClient, withFetch } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { App } from './app';
import { routes } from './app.routes';
import { expectNoAccessibilityViolations } from './testing/accessibility';

/**
 * Issue #31 - Angular 22 application shell with navigation.
 *
 * Scenario: Shell renders with primary navigation
 */
describe('App shell', () => {
  let fixture: ComponentFixture<App>;
  let router: Router;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideRouter(routes),

        // Navigating in these tests lazily loads a real view, which fetches on
        // init. Without a testing backend that request escapes to the network
        // and surfaces as an unhandled rejection that fails the run while every
        // assertion still passes - a green suite next to a red exit code.
        provideHttpClient(withFetch()),
        provideHttpClientTesting(),
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(App);
    router = TestBed.inject(Router);
    fixture.detectChanges();
  });

  afterEach(() => {
    // Requests made by a lazily-loaded view are expected; they simply must not
    // reach the network.
    TestBed.inject(HttpTestingController).match(() => true);
  });

  it('renders the header and primary navigation', () => {
    const element: HTMLElement = fixture.nativeElement;

    expect(element.querySelector('header')).toBeTruthy();
    expect(element.querySelector('nav')).toBeTruthy();
    expect(element.querySelector('main')).toBeTruthy();
  });

  it('names the navigation landmark, so a screen reader can distinguish it', () => {
    const nav: HTMLElement | null = fixture.nativeElement.querySelector('nav');

    expect(nav?.getAttribute('aria-label')).toBe('Primary');
  });

  it('links to every view', () => {
    const links: HTMLAnchorElement[] = Array.from(
      fixture.nativeElement.querySelectorAll('.shell-nav a'),
    );

    const targets = links.map((link) => link.getAttribute('href'));

    expect(targets).toContain('/instances');
    expect(targets).toContain('/workflows');
  });

  it('marks the active route with aria-current, not colour alone', async () => {
    // The visual highlight is invisible to a screen reader, and to anyone who
    // cannot distinguish the accent colour. aria-current is what actually
    // announces "you are here".
    await router.navigateByUrl('/instances');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const active: HTMLElement | null = fixture.nativeElement.querySelector(
      '.shell-nav a[aria-current="page"]',
    );

    expect(active?.getAttribute('href')).toBe('/instances');
  });

  it('offers a skip link that targets the main landmark', () => {
    // A keyboard user must be able to bypass navigation on every page. The
    // target needs tabindex="-1" or focus does not actually move.
    const skip: HTMLAnchorElement | null =
      fixture.nativeElement.querySelector('.skip-link');
    const main: HTMLElement | null = fixture.nativeElement.querySelector('main');

    expect(skip).toBeTruthy();
    expect(skip?.getAttribute('href')).toBe('#main-content');
    expect(main?.id).toBe('main-content');
    expect(main?.getAttribute('tabindex')).toBe('-1');
  });

  it('has exactly one main landmark', () => {
    // Two would make "skip to main content" ambiguous.
    expect(fixture.nativeElement.querySelectorAll('main').length).toBe(1);
  });

  it('has no accessibility violations', async () => {
    await expectNoAccessibilityViolations(fixture);
  });
});
