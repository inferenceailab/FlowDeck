import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

/**
 * Shown for an unrecognised URL.
 *
 * Deliberately a page rather than a silent redirect: a mistyped URL should say
 * so, not land somewhere plausible and leave the operator wondering.
 */
@Component({
  selector: 'app-not-found',
  imports: [RouterLink],
  template: `<h1 i18n>Page not found</h1>
    <p i18n>That URL does not match any view.</p>
    <a routerLink="/instances" i18n>Go to instances</a>`,
})
export class NotFound {}