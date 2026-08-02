/// <reference types="vite/client" />
import { describe, expect, it } from 'vitest';

/**
 * Constraints on the feature files themselves.
 *
 * Plain Vitest tests, not scenarios: these are about the specification
 * documents rather than about the dashboard, and writing them as Gherkin would
 * be a specification of the specifications.
 *
 * `vitest-cucumber` already fails the suite when a `.feature` scenario has no
 * `Scenario(...)` block, so "every scenario is implemented" needs no test here
 * - unlike the backend, where a separate count guard was necessary.
 */
describe('feature files', () => {
  // Read through Vite rather than node:fs, so this needs no Node type
  // definitions in an application tsconfig that has no other reason to carry
  // them. `eager` means the contents are inlined at build time.
  const sources: Record<string, string> = import.meta.glob('./*.feature', {
    query: '?raw',
    eager: true,
    import: 'default',
  });

  const featureFiles = (): string[] => Object.keys(sources);

  const linesOf = (path: string): string[] => sources[path].split(/\r?\n/);

  it('has at least one feature file', () => {
    // Guards every other test here: they all pass trivially against an empty
    // directory, which is the state the frontend was in before #135.
    expect(featureFiles().length).toBeGreaterThan(0);
  });

  it('tags every scenario with the issue that asked for it', () => {
    const untagged: string[] = [];

    for (const path of featureFiles()) {
      const lines = linesOf(path);

      lines.forEach((line, index) => {
        if (!line.trim().startsWith('Scenario')) {
          return;
        }

        // Tags sit above the scenario, past any blank lines.
        let tagged = false;

        for (let above = index - 1; above >= 0; above--) {
          const text = lines[above].trim();

          if (text.length === 0) {
            continue;
          }

          tagged = text.startsWith('@issue-');
          break;
        }

        if (!tagged) {
          untagged.push(`${path}: ${line.trim()}`);
        }
      });
    }

    expect(untagged).toEqual([]);
  });

  it('declares a milestone on every feature file', () => {
    const missing = featureFiles().filter((path) => !sources[path].includes('@M'));

    expect(missing).toEqual([]);
  });

  it('has no duplicate scenario titles', () => {
    const titles = featureFiles()
      .flatMap(linesOf)
      .map((line) => line.trim())
      .filter((line) => line.startsWith('Scenario:'));

    const seen = new Set<string>();
    const duplicated = titles.filter((title) => !seen.add(title));

    // Two scenarios with one title read as a copy-paste mistake, and a failure
    // naming only the title becomes ambiguous.
    expect(duplicated).toEqual([]);
  });

  it('covers every frontend scenario the issues raised', () => {
    const titles = featureFiles()
      .flatMap(linesOf)
      .map((line) => line.trim())
      .filter((line) => line.startsWith('Scenario:'));

    // Eleven from M4 (#31-#36), two from #122 whose other two scenarios are
    // observable over HTTP and live in the backend specs, three from #148,
    // five from #172, six from #181, four from #205 and four from #68.
    //
    // A hard number rather than a lower bound: it is the one thing that makes
    // deleting a scenario fail rather than quietly shrinking the suite.
    expect(titles.length).toBe(35);
  });
});
