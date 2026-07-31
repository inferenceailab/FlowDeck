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

### 5. Backend only, for now

M4's 11 scenarios are Angular behaviour — "Loading state is shown while
fetching" — and a .NET runner cannot execute them. They remain covered by the 72
Vitest specs, which name them in comments: precisely the unverified mirroring
this ADR removes everywhere else.

That inconsistency is real and is **tracked as #135** rather than left implied by
its absence.

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
