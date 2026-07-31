import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

/** One entry in the primary navigation. */
export interface NavigationItem {
  readonly path: string;
  readonly label: string;

  /** Whether the link is active only on an exact path match. */
  readonly exact: boolean;
}

/**
 * The application shell.
 *
 * Holds navigation and the routed outlet, and nothing else. Views own their own
 * data; a shell that owned any would make every view depend on it.
 */
@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  /**
   * Primary navigation.
   *
   * Declared here rather than inline in the template so a test can assert on it
   * without parsing rendered HTML, and so adding a view is a one-line change in
   * one place.
   */
  protected readonly navigation: readonly NavigationItem[] = [
    { path: '/instances', label: 'Instances', exact: false },
    { path: '/workflows', label: 'Workflows', exact: false },
  ];
}
