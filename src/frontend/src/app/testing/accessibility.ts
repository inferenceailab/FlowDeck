import { ComponentFixture } from '@angular/core/testing';
import axe, { AxeResults, Result } from 'axe-core';

/**
 * Runs axe-core against a rendered component and fails on any violation.
 *
 * ADR-0016 commits FlowDeck to WCAG 2.2 AA and to checking it in the test
 * suite rather than in an audit nobody schedules.
 *
 * **This catches roughly a third of real accessibility problems.** It finds
 * missing labels and broken semantics. It does not find a focus order that
 * makes no sense, an announcement that is technically present but useless, or a
 * control that is operable in theory and unusable in practice. Passing this is
 * necessary, never sufficient - manual keyboard verification is still part of a
 * story's Definition of Done.
 *
 * **Colour contrast is not checked here.** Angular 22 runs tests on Vitest in
 * jsdom, which has no layout engine, so axe cannot compute rendered colours and
 * skips the `color-contrast` rule entirely. The contrast ratios in
 * `styles.css` are therefore asserted by comment, not by test - a real gap in
 * what ADR-0016 promised. Closing it needs either a browser-backed test run
 * (`@vitest/browser-playwright`) or a separate contrast check.
 */
export async function expectNoAccessibilityViolations(
  fixture: ComponentFixture<unknown>,
): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();

  const results: AxeResults = await axe.run(fixture.nativeElement as Element, {
    // The standard this project committed to. Restricting the rule set means a
    // failure is a genuine AA violation rather than best-practice advice, so
    // nobody learns to ignore the output.
    runOnly: {
      type: 'tag',
      values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'],
    },
  });

  if (results.violations.length > 0) {
    throw new Error(formatViolations(results.violations));
  }
}

/**
 * Turns axe output into something actionable.
 *
 * The default shape is a large object that a test runner prints as `[object
 * Object]`, which tells a developer nothing about what to fix.
 */
function formatViolations(violations: Result[]): string {
  const lines = violations.map((violation) => {
    const targets = violation.nodes
      .map((node) => `      ${node.target.join(' ')}`)
      .join('\n');

    return [
      `  ${violation.id} (${violation.impact ?? 'unknown impact'})`,
      `    ${violation.help}`,
      `    ${violation.helpUrl}`,
      targets,
    ].join('\n');
  });

  return `${violations.length} accessibility violation(s):\n${lines.join('\n\n')}`;
}
