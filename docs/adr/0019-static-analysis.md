# ADR-0019: SonarAnalyzer, with warnings as errors from a clean tree

**Status:** Accepted · **Milestone:** post-M4

## Context

GitHub Code Quality — CodeQL's maintainability and reliability analysis — is
**not available** on this repository. It is generally available on Enterprise
Cloud and Team, and its documentation requires that "an enterprise owner must
have allowed Code Quality in your enterprise". FlowDeck is a public repository
on a free personal account, so there is no enterprise to allow it. Verified: the
repository's settings have no **Code quality** entry and `/settings/code_quality`
returns 404.

CodeQL security scanning *is* enabled and now covers five languages. What is
missing is the maintainability half.

[ADR-0010](0010-minimise-third-party-dependencies.md) requires a third-party
dependency to be justified rather than assumed.

## Decision

**`SonarAnalyzer.CSharp`, applied repository-wide via `Directory.Build.props`,
with `TreatWarningsAsErrors` enabled.**

- One `PackageReference` at the repository root, so a new project inherits the
  analysis rather than needing to remember it.
- `PrivateAssets="all"` — an analyser is a build-time tool and must never flow
  to a consumer of `FlowDeck.Core`, which would impose these rules on anyone
  referencing the package.
- `AnalysisMode` stays at the SDK **default**, not `Recommended`. Recommended
  switches on a large set of Microsoft CA rules nobody asked for, and mixing
  them in makes it impossible to tell which findings came from this decision.
- Test projects relax four rules in `tests/Directory.Build.props`, each with a
  stated reason. `CA1707` is the notable one: test names here are sentences,
  which is deliberate and makes a failure report readable.

### Why warnings as errors, and why only now

Enabling it over an unanalysed codebase breaks the build until every finding is
triaged, which pressures whoever hits it into suppressing rules wholesale. That
is how a quality gate becomes a formality.

So the order was: **add the analyser, fix what it found, then ratchet.** It
reported 44 findings; all were addressed. From zero, every new finding is a
build failure at the moment it is written.

A warning nobody must act on is a warning everybody scrolls past.

## What it found

Mostly small — redundant null-forgiving operators, parameter names diverging
from the interfaces they implement, a stateless class that should be static.
Two were worth the exercise on their own:

**A test that proved nothing.** `The_document_is_served_outside_Development` was
byte-identical to the test above it (S4144). `WebApplicationFactory` hosts in
the Development environment by default, so a test named for behaviour *outside*
Development ran inside it. Its name promised something it could not check. Now
it sets the environment explicitly.

**A high-severity vulnerability**, surfaced by the same build:
`Microsoft.OpenApi` 2.0.0 ([GHSA-v5pm-xwqc-g5wc][adv]) arrived transitively with
`Microsoft.AspNetCore.OpenApi` in #28 and had been in the tree since.
Upgrading the wrapper did not help — it still pinned 2.0.0 — so the transitive
is now pinned directly to 2.11.0.

## Consequences

- Maintainability findings are caught locally and in CI, closing most of the gap
  Code Quality would have filled.
- **A build failure is now the cost of a new finding.** That is the point, and
  it will occasionally be irritating; the escape hatch is a targeted `NoWarn`
  with a written reason, never a blanket suppression.
- One more build-time dependency, and one more thing to keep current.
- Sonar's rules are opinionated. Where a rule is wrong for this codebase the
  answer is to disable it explicitly and say why, as `tests/Directory.Build.props`
  does — not to work around it in the code.
- **This is not equivalent to GitHub Code Quality.** There are no
  maintainability or reliability *scores*, no dashboard, no AI-assisted
  findings, and no per-pull-request quality gate. It is the analyser, not the
  product.

## Alternatives considered

**Nothing.** Leaves the maintainability gap open with no compensating control.

**Wait for GitHub Code Quality.** Requires a plan change for a single-developer
repository. The gap is fillable today for the price of one build-time package.

**`AnalysisMode=Recommended` instead of Sonar.** Free, in the SDK, no new
dependency — and a narrower rule set than Sonar for the maintainability and
reliability findings that motivated this. It was tried first and produced mostly
`CA1707` noise about test naming.

**Sonar rules as warnings, not errors.** Where this started. Rejected once the
tree was clean: findings nobody must act on accumulate until the build output is
unreadable, which is worse than not running the analyser.

[adv]: https://github.com/advisories/GHSA-v5pm-xwqc-g5wc
