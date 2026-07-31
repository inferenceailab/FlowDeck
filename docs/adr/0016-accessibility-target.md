# ADR-0016: Accessibility target is WCAG 2.2 AA, checked in CI

**Status:** Accepted · **Milestone:** M4 · **Issue:** #62

## Context

The Phase 1 breakdown produced six frontend stories and **none of them mentioned
accessibility**. That gap was found by review, not by planning, and #62 exists
to close it before the dashboard is built rather than after.

Deciding late is the expensive path. Retrofitting keyboard navigation, focus
management and semantics into an existing component tree means revisiting every
template; deciding now costs almost nothing.

## Decision

**WCAG 2.2 AA**, with automated checks in the test suite.

Concretely, and in priority order:

1. **Keyboard operable.** Every action reachable and usable without a mouse.
   An operator cancelling a runaway workflow at 2am may be on a laptop with a
   broken trackpad; this is not a hypothetical.
2. **Semantic HTML first.** A `<button>` before a `<div role="button">`. ARIA is
   a repair, not a starting point.
3. **Status is never colour alone.** `Failed` and `Completed` must differ by
   text or icon as well as colour — the dashboard's primary job is conveying
   status, and roughly one in twelve men has a colour vision deficiency.
4. **Visible focus** that survives custom styling.
5. **Live regions** for the auto-updating list (#36), so a status change is
   announced rather than silently repainting.

Verified with **axe-core** run against rendered components in the unit test
suite. A violation fails the build.

## Consequences

- Accessibility failures are caught by `npm test`, not by a manual audit that
  never gets scheduled.
- **Automated checks catch perhaps a third of real issues.** They find missing
  labels and contrast failures; they do not find a focus order that makes no
  sense or an announcement that is technically present but useless. Claiming
  "axe passes, therefore accessible" would be the failure mode this ADR is
  meant to avoid.
- Manual keyboard-only walkthroughs are still required before calling a view
  done, and the Definition of Done for M4 stories says so.
- **Colour contrast is not actually checked**, discovered when implementing
  #31. Angular 22 runs tests on Vitest in jsdom, which has no layout engine, so
  axe cannot compute rendered colours and skips the `color-contrast` rule.
  The ratios in `styles.css` are asserted by comment, not by test. Closing this
  needs a browser-backed run (`@vitest/browser-playwright`, which pulls in a
  browser download) or a standalone contrast check. Recorded rather than left
  implied by "axe runs in CI".
- Some component choices are constrained — a custom dropdown that cannot be
  operated by keyboard is not an option.
- WCAG 2.2 AA is a *floor*, not a ceiling.

## Alternatives considered

**No stated target.** The status quo, and the reason this ADR exists. "We care
about accessibility" without a testable definition means nobody can tell whether
a change made it worse.

**WCAG 2.2 AAA.** Includes requirements — 7:1 contrast, sign language for media
— that are disproportionate for an internal operator dashboard and would be
quietly ignored, which is worse than not claiming them.

**Manual audits only.** Catches more than automation, and happens once and then
never again. The regression is invisible until someone complains.

**Automated checks only, no manual verification.** Would let this project claim
a standard it has not met. Automated tooling cannot judge whether a focus order
is sensible.
