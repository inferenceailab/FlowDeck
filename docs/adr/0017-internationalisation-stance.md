# ADR-0017: Mark text for translation now, ship English only

**Status:** Accepted · **Milestone:** M4 · **Issue:** #62

## Context

The frontend stories said nothing about internationalisation — the same gap
ADR-0016 addresses for accessibility.

The costs are asymmetric in a way that makes "decide later" the worst option:

| Work | Cost now | Cost retrofitted |
| --- | --- | --- |
| Marking text with `i18n` attributes | trivial, as templates are written | **touch every template** |
| Extraction and build per locale | real | same |
| Translating | same | same |

Deferring i18n entirely means paying the expensive half later. Adopting it fully
means paying the build complexity for a second locale nobody has asked for.

## Decision

**Mark translatable text from the first template. Ship one locale: English.**

- Angular's built-in `i18n` attributes on user-visible text as templates are
  written.
- **No locale build configuration, no translation files, no locale switcher.**
  Those arrive with a second locale, not before.
- Dates and numbers go through Angular's `DatePipe` and `DecimalPipe` rather
  than hand-formatted strings, so locale-aware formatting is a configuration
  change rather than a rewrite.
- Timestamps are rendered in the **viewer's local time** from the UTC values the
  API returns. An operator reasons in their own timezone; the API remains UTC so
  instances stay orderable.

## Consequences

- Adding a locale later means extraction and translation — the work that
  genuinely cannot be avoided — not a sweep through every template.
- `i18n` attributes are visible in templates and mean nothing today. That is the
  cost: a reader may wonder why they are there. This ADR is the answer.
- No build complexity, no bundle-per-locale, no runtime locale negotiation until
  they are actually needed.
- **A contributor who forgets an `i18n` attribute creates the exact debt this
  avoids.** Nothing enforces it: Angular's extractor reports unmarked text only
  when extraction runs, which is not configured. Accepted, and recorded as the
  weak point.

## Alternatives considered

**English-only, i18n deferred entirely.** Simplest today. Pays the expensive
half — touching every template — later, and by then there are more templates.

**Full i18n from the start, with a second locale.** Would prove the pipeline
works. Requires translations nobody has asked for, and an untranslated locale is
worse than none.

**A runtime translation library** (`ngx-translate` or similar) rather than
Angular's build-time i18n. Simpler locale switching, and a third-party
dependency where the framework already has an answer — against ADR-0010.

## Revisit when

A second locale is actually wanted. At that point this ADR is superseded by one
covering extraction, build configuration and locale selection.
