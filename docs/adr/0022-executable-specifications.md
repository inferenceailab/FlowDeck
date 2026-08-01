# ADR-0022: Feature files are executable, not decorative

**Status:** Accepted · **Milestone:** post-M5 · **Issues:** #130, #131

## Context

The brief binds this project to BDD: every user story carries Given/When/Then
acceptance criteria. That has been honoured in language and not in machinery.

Scenarios were written into GitHub issues, then mirrored **by hand** into XML doc
comments on xUnit test classes:

```csharp
/// <summary>
/// Issue #105 - Back off between attempts, with jitter.
///
/// Scenario: Exponential backoff grows the delay
/// </summary>
public class BackoffTests
```

Nothing connects the comment to the test, and nothing fails when they diverge.
Counting the two sides at the point this was written:

| | |
| --- | --- |
| `.feature` files in the repository | **0** |
| Scenarios written in issues | **90** |
| Scenarios named in a test doc comment | **61** |

**29 scenarios had no test that even claimed to cover them.** The other 61 were
matched by a comment nobody verified — the same failure mode this repository has
repeatedly caught elsewhere: an artefact that looks like a guarantee and is not.

## Decision

**Feature files execute.** Adopt [Reqnroll](https://reqnroll.net) 3.3.4, the
maintained successor to SpecFlow, in a new `tests/specs/FlowDeck.Specs` project.
Each Given/When/Then binds to C#, so a scenario runs the code it describes.

### 1. An unimplemented step fails the build

Verified before committing to the tool, because it is the entire premise. A
feature file containing a step no binding matches produces:

```
Failed!  -  Failed: 1, Passed: 1, Skipped: 0
exit code: 1
```

Not skipped. A green tick meaning "not run" is exactly what this replaces — the
same reasoning that made the store conformance suite use `[SkippableFact]` so an
unconfigured database reports as skipped rather than passed.

### 2. The existing unit tests stay

All 398 of them, unchanged. The specifications are an acceptance layer *above*
them, not a replacement.

Rewriting them as step definitions would cost a great deal and lose something
specific: the comments explaining *why* each test exists, which are frequently
the most valuable line in the file. A specification says what the system does; a
unit test can also say what a past mistake was.

The two layers answer different questions. `Every_jittered_delay_stays_within_the_policy_bounds`
is a property, not a scenario, and would read poorly as Gherkin.

### 3. Traceability by tag, not by folder

Scenarios carry `@issue-105` and a milestone tag. Feature files are organised by
capability, because that is how someone reads them; the tag carries the link back
to the issue that asked for it.

### 4. Feature files use current vocabulary, not the vocabulary of the issue

Issue #2's Gherkin says `IStepBody` and `Outcome.Persist`. ADR-0012 renamed those
to `IStep` and `Outcome.Suspend` before either shipped.

The feature files use the current names. A specification that will not compile
against the code it specifies is worse than no specification, and the issue is a
historical record rather than a live contract.

Where a scenario's *wording* has been adjusted this way, the change is to
vocabulary only, never to what is being asserted.

### 5. The frontend uses vitest-cucumber, in the same Vitest run

**Decided by the maintainer, 2026-08-01.**

A .NET runner cannot execute "Loading state is shown while fetching", so the 13
frontend scenarios use [`@amiceli/vitest-cucumber`](https://www.npmjs.com/package/@amiceli/vitest-cucumber)
inside the existing `ng test` run. No second test stack, no browser driver, and
`npm test` still runs everything.

The same premise was checked first: a scenario present in a `.feature` file with
no `Scenario(...)` block raises `ScenarioNotCalledError`, fails the suite, and
exits non-zero.

**A constraint worth knowing before writing one.** `vitest-cucumber` runs each
step as its own Vitest test, and Angular resets `TestBed` between tests. A
`ComponentFixture` created in a Given is already torn down by the time the When
runs — `detectChanges()` renders into nothing and the Then sees an empty DOM,
while every step reports green.

Measured, not assumed: a probe rendering in one step and asserting in the next
fails, and `teardown: { destroyAfterEach: false }` does not change it.

So frontend steps follow one shape, and `harness.ts` exists to make it readable:

- **Given** records what the world contains, as plain values.
- **When** builds the component, renders it, answers its requests, performs the
  interaction, and captures the resulting DOM.
- **Then** asserts against what the When captured.

Plain values and detached DOM nodes survive the reset; Angular objects do not.
This reads slightly oddly for a Given like "a suspended instance is displayed",
which records rather than displays — and that is the honest trade for scenarios
that assert on something real.

The alternative, `@cucumber/cucumber` with Playwright, would have avoided the
constraint by driving a real browser. Rejected as a second test stack and a
browser download in CI, for scenarios that are about rendering rather than about
end-to-end behaviour.

## Consequences

- A scenario and the code cannot drift silently. Renaming a concept breaks the
  feature file that describes it.
- The 29 uncovered scenarios become visible as failures rather than as nothing
  at all.
- Reqnroll is a substantial dependency with MSBuild code generation, which sits
  against [ADR-0010](0010-minimise-third-party-dependencies.md)'s instinct.
  Justified on the same test that ADR applies: Gherkin parsing, binding and
  execution is genuinely substantial, and hand-rolling it would be the reckless
  end of that judgement, not the frugal end.
- Two test paradigms coexist. Someone adding a feature has to know which layer a
  new assertion belongs in — the guidance is that a scenario an operator or
  author would recognise goes in a feature file, and everything else stays a
  unit test.
- Feature files are readable by someone who does not read C#. That was always
  claimed of the issue bodies and was never true of anything in the repository.
- Two Gherkin runners now exist in one repository, with different step syntax
  and different constraints. Unavoidable given a .NET backend and an Angular
  frontend, and the cost is that "how do I add a scenario" has two answers.
- The frontend scenarios reach the DOM but not a browser. They assert what a
  component renders, not what a user sees after CSS and layout. Anything that
  depends on real paint or real navigation is out of their reach.

## Alternatives considered

**Feature files plus a traceability test, with no runner.** Write real `.feature`
files and add a test asserting every `Scenario:` has a test method naming it.
Zero new dependencies, and it would have closed the 29-scenario gap.

Rejected because it checks that a scenario *exists*, not that the test performs
those steps. A feature file could describe Given/When/Then the test never does —
a document that looks executable and is not, which is the problem restated rather
than solved.

**Migrating the existing acceptance tests into Gherkin.** The fullest form of the
practice. Rejected on cost and on the loss described in decision 2.

**Xunit.Gherkin.Quick.** Lighter than Reqnroll and adequate for simple binding.
Rejected for weaker tooling around hooks, scenario context and data tables, all
of which the persistence and API scenarios need.
