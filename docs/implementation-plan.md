# FlowDeck — Implementation Plan

## Approach

Milestones are decomposed **progressively**: near-term milestones carry full
BDD user stories, later ones carry epic placeholders that state their intent,
what must land before they can be decomposed, and the open design questions.

The reason is that a Given/When/Then is a behavioural contract. Writing fifty
of them against an architecture that does not exist produces confident-looking
fiction. M6's stories cannot be written until M2 decides whether instance
claiming is lease-based or version-based — so they are not.

Every story follows: **failing test → minimal implementation → refactor →
PR closing the issue**. Where a story turns out to be already satisfied by
earlier work, that is reported rather than staged as a false RED.

## Status

| Milestone | Scope | Issues | Status |
| --- | --- | --- | --- |
| Phase 1 | Project management and security | — | ✅ Complete |
| Phase 2 | CI/CD infrastructure | #44 | ✅ Complete |
| **M1** | **Core Engine Primitives** | **#1–#12** | ✅ **Complete (12/12)** |
| M2 | Persistence & Recovery | #13–#22 | Next |
| M3 | Minimal API Surface | #23–#30 | Not started |
| M4 | Dashboard Skeleton | #31–#36 | Not started |
| M5 | Retries & Error Handling | #37, #38 | Epic only |
| M6 | Distributed Execution | #39 | Epic only |
| M7 | Visual Designer | #40 | Epic only |
| M8 | Observability | #41 | Epic only |
| M9 | Production Hardening | #42, #43 | Epic only |

M1–M4 together form one vertical slice: define a workflow in C# → execute it →
survive a restart → drive it over HTTP → watch it in a dashboard.

## M1 — Core Engine Primitives ✅

81 tests. Three stories (#7, #8, #9) required no production code — earlier work
already satisfied them, and their PRs say so rather than manufacturing a RED
phase.

| Issue | Story | Outcome |
| --- | --- | --- |
| #1 | Definition with stable identifier | `IWorkflowDefinition`, `WorkflowRegistry` |
| #2 | Step as atomic unit of work | `IStep`, `Outcome`, `StepExecutor` |
| #3 | Single-step workflow completes | `WorkflowEngine`, `WorkflowInstance` |
| #4 | Steps in declared sequence | Tests only — loop already correct |
| #5 | Data between steps | `IWorkflowData` |
| #6 | Step failure as workflow failure | `FailedStepName` |
| #7 | Unique instance identifier | Tests only |
| #8 | Lifecycle timestamps | Tests only — `TestTimeProvider` |
| #9 | Reject unregistered definition | Tests only |
| #10 | Typed workflow input | `IWorkflowDefinition<TInput>` |
| #11 | Query in-flight instance | `IInstanceStore` |
| #12 | Cancel an instance | `Cancel`, `ResumeAsync` |

**Scope note:** #12 added `ResumeAsync`, slightly beyond its stated scope. The
clause "no further steps execute" is untestable without a way to attempt
continuation, and suspension had had no counterpart since #2.

## M2 — Persistence & Recovery (next)

The milestone that makes FlowDeck real. Everything M1 built is lost on restart.

Sequencing matters here:

1. **#16 in-memory provider + conformance suite** first. A shared contract test
   is what makes #17 verifiable rather than hopeful.
2. **#13 persist after every step**, **#15 persist workflow data** — the write path.
3. **#14 resume after restart** — the read path, and the milestone's real goal.
4. **#17 EF Core provider** against the conformance suite from #16.
5. **#18 execution history**, **#19 concurrency detection** — needed by M6.
6. **#20 purge**, **#21 migrations**, **#22 crash mid-step** — operational.

**Open question to settle first:** does persistence store a checkpoint per step
or an append-only event log? #19 and #39 both depend on the answer, and it
should be an ADR before #13 is written.

Expect `IInstanceStore` to change — async signatures, concurrency tokens, query
predicates. That was anticipated in
[ADR-0009](adr/0009-in-memory-store-is-temporary.md).

## Blocked work

| Blocker | Impact |
| --- | --- |
| **No self-hosted runner registered** | Every CI/CD run queues indefinitely. All test results to date are from local runs; the pipeline has never verified anything. |
| CodeQL detected `languages: []` | Configured before any code existed. Should pick up C# now — needs checking. |

## Process debt

Recorded so it is not repeated.

| Item | Fix |
| --- | --- |
| The Phase 1 breakdown created 43 issues, none for documentation | #58; future milestones include documentation stories |
| ADRs were written retrospectively after M1 | Future ADRs ship in the same PR as the change |
| Frontend milestones have no stories for accessibility or i18n | Add before M4 begins |

## Definition of Done

A story is done when: BDD scenarios exist as tests; those tests failed before
implementation, or the PR explains why they could not; `dotnet test` and
`dotnet format --verify-no-changes` pass; non-obvious decisions have an ADR;
and it merged via a PR closing the issue.
