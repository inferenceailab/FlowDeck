# Architecture Decision Records

An ADR records a decision that was not obvious, together with the reasoning and
what it costs. If a decision could reasonably have gone the other way, it
belongs here.

These were written retrospectively after M1, from the reasoning captured in
pull request descriptions. That is a gap in the process — decisions should be
recorded as they are made, not reconstructed — and the fix is that future
milestones write the ADR in the same pull request as the change.

## Format

Each record states **Context**, **Decision**, **Consequences** and
**Alternatives considered**. Consequences include the costs, not only the
benefits; an ADR listing only upsides is advocacy, not a record.

## Status values

| Status | Meaning |
| --- | --- |
| Accepted | In force |
| Superseded | Replaced, with a link to the replacement |
| Deprecated | No longer applies, not replaced |

## Index

| # | Decision | Status | Milestone |
| --- | --- | --- | --- |
| [0001](0001-definition-identity-includes-version.md) | Definition identity includes version | Accepted | M1 |
| [0002](0002-step-bodies-from-factories.md) | Step bodies come from factories | Accepted | M1 |
| [0003](0003-step-executor-trust-boundary.md) | StepExecutor is the trust boundary | Accepted | M1 |
| [0004](0004-cancellation-is-not-step-failure.md) | Cancellation is not step failure | Accepted | M1 |
| [0005](0005-workflow-data-is-checked-not-cast.md) | Workflow data reads are checked | Accepted | M1 |
| [0006](0006-input-type-is-declared.md) | Input type is declared, not reflected | Accepted | M1 |
| [0007](0007-record-instance-before-execution.md) | Instances are recorded before execution | Accepted | M1 |
| [0008](0008-terminal-states-are-final.md) | Terminal states are final | Accepted | M1 |
| [0009](0009-in-memory-store-is-temporary.md) | The in-memory store is temporary | Accepted | M1 |
| [0010](0010-minimise-third-party-dependencies.md) | Minimise third-party dependencies | Accepted | Phase 2 |
