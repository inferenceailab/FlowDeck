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
| **M2** | **Persistence & Recovery** | **#13–#22** | ✅ **Complete (10/10)** |
| **M3** | **Minimal API Surface** | **#23–#30** | ✅ **Complete (8/8)** |
| M4 | Dashboard Skeleton | #31–#36 | Next |
| M5 | Retries & Error Handling | #37, #38 | Epic only |
| M6 | Distributed Execution | #39 | Epic only |
| M7 | Visual Designer | #40 | Epic only |
| M8 | Observability | #41 | Epic only |
| M9 | Production Hardening | #42, #43, #67 | Epic only |
| M10 | Operator Control | #66, #68 | Epic only |

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

## M2 — Persistence & Recovery ✅

223 tests. Four stories (#14, #19, #22, and the scenario half of #15) required
no production code — earlier work already satisfied them, and their PRs say so.

| Issue | Story | Outcome |
| --- | --- | --- |
| #16 | In-memory provider + conformance suite | `IWorkflowStore`; the suite is the contract |
| #13 | Persist after every step | Engine checkpoints; API became async |
| #14 | Resume after restart | Tests only — #13 had already enabled it |
| #15 | Persist workflow data | `WorkflowDataSerializer`, type allow-list |
| #18 | Append-only execution history | Engine writes history atomically with state |
| #19 | Concurrency detection | Tests only — two defences, either may fire |
| #22 | Crash mid-step | Tests only — modelled with a store that stops writing |
| #20 | Purge after retention | `RetentionPolicy`, `InstancePurger` |
| #17 | EF Core provider | Verified by the same suite, on SQLite |
| #21 | Schema migrations | `WorkflowStoreMigrator`; ADR-0015 |

**Decisions:** [ADR-0013](adr/0013-persistence-model.md) (checkpoint + history),
[ADR-0014](adr/0014-workflow-data-serialisation.md) (serialisation),
[ADR-0015](adr/0015-migrations-are-owned-by-the-host.md) (migrations).

**Found while building, not by planning:**

- A crashed instance is stuck in `Running` with no sweep to recover it (#39).
- SQLite refuses to `ORDER BY` a `DateTimeOffset` — caught by the conformance
  suite running against a second provider.
- `SQLitePCLRaw` arrived transitively with a high-severity CVE; pinned.
- A `dotnet format` violation from #14 reached `main` because I skipped the
  format check on that story.

**Carried forward:** #78 (verify EF Core against PostgreSQL, not just SQLite).
It needs either a Docker dependency or a database, so it is a decision rather
than a task.

## M3 — Minimal API Surface ✅

285 tests (62 API, 223 core). Every story required production code — no
characterization-only stories this milestone, unlike M1 and M2.

| Issue | Story | Outcome |
| --- | --- | --- |
| #23 | Start an instance over HTTP | API scaffold; 202 with `Location` |
| #24 | Query one instance | `InstanceResponse` projection, no stack traces |
| #25 | List with paging and filtering | `total` ignores paging; `pageSize` capped |
| #26 | Cancel over HTTP | `POST /cancel`, 409 on terminal |
| #27 | RFC 9457 problem details | Stable `type` URIs, `traceId` |
| #28 | OpenAPI document | Asserted to cover every routed endpoint |
| #29 | Health and readiness | Liveness deliberately independent of the store |
| #30 | List definitions | Read-only; scenario, not the title |

**Found while building, not by planning:**

- A malformed JSON body returned **500**, telling a client to retry against a
  server that was working perfectly.
- A routing 404 returned an **empty body** — clients would have received problem
  details for some errors and nothing for others.
- Collection routes registered as `MapGet("/")` produced paths with a trailing
  slash that the OpenAPI document normalised away.

**Scope note:** #30 is titled "register a definition over HTTP" but its scenario
only requires listing. Definitions are C# classes registered at startup, so
there is nothing to POST; authoring over the wire is #40's question.

**Carried forward:** the API has **no authentication** (#42), cannot resume a
suspended instance (#68), and does not expose execution history at all despite
the engine recording it.

## M2 — original sequencing notes

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

## Backlog gaps found after M1

A review on 2026-07-31 asked whether sagas, compensation, versioning and
management actions were in the plan. Three of the four were not, or not
properly. Recorded here because the pattern matters more than the individual
gaps: the Phase 1 breakdown was decomposed **milestone by milestone**, which
covers depth well and cross-cutting product areas badly.

| Topic | Was it covered? | Action |
| --- | --- | --- |
| **Compensation** | Yes - #38, with real open questions | None |
| **Sagas** | Title-deep only. #38 said "saga" in its title; its body was entirely compensation | Scope question added to #38: does FlowDeck coordinate *external* participants, or only undo its own steps? Never decided. |
| **Versioning** | Split badly. Identity versioning done in M1 ([ADR-0001](adr/0001-definition-identity-includes-version.md)); *migration of in-flight instances* was two bullets inside #43, an epic also covering multi-tenancy and performance | Split into #67 as its own epic. #43 rescoped. |
| **Management actions** | **No.** Cancel was the only operator action in the entire backlog | New milestone M10 and epic #66. `ResumeAsync` shipped with no story at all - #68. |

The `ResumeAsync` case is the sharpest: it is a public engine API that exists
because #12 needed it to prove a scenario clause, and it has no acceptance
criteria, no HTTP endpoint and no dashboard exposure. A suspended workflow is
currently only completable from inside the process that started it.

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
| Four product areas were under-covered or mis-filed - found by review, not by planning | #66, #67, #68 and a scope question on #38 |

## Definition of Done

A story is done when: BDD scenarios exist as tests; those tests failed before
implementation, or the PR explains why they could not; `dotnet test` and
`dotnet format --verify-no-changes` pass; non-obvious decisions have an ADR;
and it merged via a PR closing the issue.
