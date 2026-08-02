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
| **M4** | **Dashboard Skeleton** | **#31–#36, #62, #92** | ✅ **Complete (8/8)** |
| **M5** | **Retries & Error Handling** | **#37, #38, #103–#108, #118–#123** | ✅ **Complete (14/14)** |
| **M6** | **Distributed Execution** | **#39, #143–#150** | ✅ **Complete (9/9)** |
| **M7** | **Branching, Parallel Execution & Visualisation** | **#40, #161–#167, #171, #172, #181** | ✅ **Complete (11/11)** |
| **M8** | **Observability** | **#41, #185–#190** | ✅ **Complete (7/7)** |
| **M9** | **Production Hardening** | **#43, #67, #202–#207** | ✅ **Complete (8/8)** |
| **M10** | **Operator Control** | **#66, #68, #124, #179, #216–#220** | ✅ **Complete (9/9)** |
| M11 | Authentication & Authorisation | #42 | Epic only |

M1–M4 together form one vertical slice: define a workflow in C# → execute it →
survive a restart → drive it over HTTP → watch it in a dashboard.

M5–M7 make that slice survive contact with reality: steps that fail and are
retried, work that must be undone, more than one node, and workflows that are
not straight lines.

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
there is nothing to POST; authoring over the wire is #183's question — it was
#40's until M7 delivered that epic's read-only half and closed it.

**Carried forward:** the API has **no authentication** (#42), cannot resume a
suspended instance (#68), and does not expose execution history at all despite
the engine recording it.

## M4 — Dashboard Skeleton ✅

53 frontend tests, 293 backend. Angular 22.1, Vitest, axe-core.

| Issue | Story | Outcome |
| --- | --- | --- |
| #62 | Frontend decisions | ADR-0016, ADR-0017, ADR-0018 — written **before** any template |
| #92 | Execution history over HTTP | Found by writing the architecture first; #33 had no data source |
| #31 | Application shell | Skip link, landmarks, `aria-current`, axe harness |
| #32 | Instance list | Generated API client; table with caption and scoped headers |
| #34 | Loading, empty, error states | `LoadState` union; problem-details messages surfaced |
| #33 | Detail view and timeline | Failure stated above the evidence, not buried in it |
| #35 | Cancel from the dashboard | Confirmation gate; disabled not hidden |
| #36 | Live updates | Five-second polling; in-flight guard; timer cleared on destroy |

**Found while building, not by planning:**

- **`InstanceStatus` serialised as an integer.** The API returned `"status": 2`
  — unreadable in a dashboard, and an ordinal that would silently change meaning
  if a status were inserted mid-enum. No backend test could have caught it;
  only writing a client did.
- **No DTO appeared in `components.schemas`.** Endpoints returned `IResult`, so
  OpenAPI inlined every response type anonymously and the document was nearly
  useless for the generation #28 exists to enable.
- **The CI test command was wrong.** Written in Phase 2 against Karma;
  Angular 22 runs Vitest and would have failed on the first frontend run.
- **`i18n` attributes need `@angular/localize`**, uninstalled — ADR-0017's cost
  arriving on the first template.
- **A spec navigated into a lazily-loaded view that fetched**, escaping to the
  network as an unhandled rejection: every assertion passed while the run exited
  non-zero.

**Honest limitation:** colour contrast is **not** verified by test. jsdom has no
layout engine, so axe skips `color-contrast` and the ratios in `styles.css` are
asserted by comment. Recorded in ADR-0016 rather than left implied by "axe runs
in CI".

**Carried forward:** the dashboard has no paging controls, so only the newest 50
instances are reachable in the UI; no workflow-definitions view; and no way to
resume a suspended instance (#68).

## M5 — Retries & Error Handling ✅

Two epics in one milestone, because they are the same subject from both ends:
what to do when a step fails, and what to do about the steps that already
succeeded.

| Issue | Story | Outcome |
| --- | --- | --- |
| #37 | Retry epic | [ADR-0020](adr/0020-retry-semantics.md) — written before #103 |
| #103 | Retry policy on a step | `RetryPolicy`, `WithRetryPolicy` |
| #104 | Workflow-wide default | Forward-applying, unlike `WithCompensation` |
| #105 | Backoff with jitter | Blocks the calling task — still open as #39's neighbour |
| #106 | Attempt count survives restart | `StepAttempts` persisted |
| #107 | Every attempt in history | Attempt numbers from 1; carried to the dashboard |
| #108 | Idempotence documented | Prose asserted against the file, not just examples compiled |
| #38 | Compensation epic | [ADR-0021](adr/0021-compensation-semantics.md) |
| #118 | Declare a compensating action | Backwards-applying; null, not a no-op action |
| #119, #120, #121 | Roll back on failure | Shipped together — see below |
| #122 | Compensation over HTTP and in the dashboard | Two new terminal statuses reach the UI |
| #123 | Compensation documented | Including that it is best-effort |

**#119, #120 and #121 shipped as one PR deliberately.** Rollback and its terminal
status are one mechanism: #119 alone would be a rollback reporting the wrong
status, and a test asserting `Failed` on a fully compensated instance would have
been written and immediately deleted.

**Found while building, not by planning:**

- **A mutation test found a decision that nothing tested.** With #119 and #120
  green, replacing "continue past a failing compensating action" with "stop at
  the first" left all 29 tests passing — a choice ADR-0021 had made explicitly.
  A decision nothing tests is a comment. #121 was pulled forward into the same
  PR; the same mutation now fails three tests.
- **Idempotence became load-bearing in a second place.** #108 required it for
  retries. #38 then relied on it to justify compensating a step *once* however
  many attempts it made — the attempts shared one key and therefore one side
  effect.
- **`int32` generates as `number | string`** in the client, because the served
  OpenAPI document declares it as either. Coerced in the component rather than
  compared loosely in a template, where `'2' > 1` would be a string comparison
  doing the right thing for the wrong reason.

**Carried forward:** a retry backoff still blocks the calling task, and there is
no dead-letter or give-up-and-park outcome.

## M6 — Distributed Execution ✅

The milestone that made the "one instance, one worker" comment true rather than
hopeful.

| Issue | Story | Outcome |
| --- | --- | --- |
| #39 | Multi-node epic | [ADR-0023](adr/0023-multi-node-execution.md) |
| #143 | Owner and lease on an instance | On the instance record, not a separate store |
| #144, #145 | Claim and renew | Built on `SaveAsync` and its `Revision` guard alone |
| #146, #147 | Recover abandoned work | Per-node dispatcher; no leader, no election |
| #148 | Show the owning node | `awaitingRecovery` computed server-side |
| #149 | Release leases on shutdown | Clean stop hands work back rather than waiting for expiry |
| #150 | Multi-node documented | Including the duplicate-execution window |

**Claiming needed no new provider surface.** Two nodes that read the same
instance and both write are already resolved by the concurrency token the
conformance suite enforces on every provider, so atomic claiming inherited a
guarantee that was already tested.

**Found while building, not by planning:**

- **Every checkpoint was silently wiping the lease.** `ToRecord` did not carry
  `OwnerNodeId` or `LeaseExpiresAt`, so a node claimed an instance, ran a step,
  checkpointed and lost its claim — a peer could take the instance out from
  under it mid-run. The scenario asserting the dispatcher *releases* the lease
  had been passing the whole time, for the wrong reason. Found by mutation
  testing, not by a failing test.
- **A scenario was not testing its own name.** "The dispatcher survives a
  failing instance" passed against a mutation deleting the exception handling
  entirely, because a failing workflow never throws — the engine records the
  failure and returns.
- **An unreachable store took the dispatcher loop down with it**, which would
  have left every node permanently idle while still reporting itself alive. The
  catch is now deliberately broad, with the reason written down and failures
  counted rather than swallowed.
- **Two scenarios were never running at all** — they did not appear in
  `--list-tests`.

**Honest limitation:** a lease lapsing while its owner is still working lets two
nodes execute the same step. Fencing on `Revision` means only one records
progress, which bounds the damage without preventing it. That is a real
weakening of NFR-1, recorded in ADR-0023 rather than left implied.

**Carried forward:** recovery is not load balancing — an instance started on a
busy node stays there.

## M7 — Branching, Parallel Execution & Visualisation ✅

**The milestone was renamed.** It began as "Visual Designer". Workflows were
strictly linear, so a canvas would have been drawing a straight line and
implying engine features that did not exist. The engine work comes first and the
visual view second, which is what #161 exists for and why #40 could not have been
decomposed before it.

| Issue | Story | Outcome |
| --- | --- | --- |
| #161 | Branching epic | [ADR-0024](adr/0024-branching-and-parallel-execution.md) |
| #162 | Declare a branch and a fork | `Branch`, `BranchWhen`, `Fork`; names unique graph-wide |
| #163 | Set-valued position | `ActiveNode`; `CurrentStepIndex` becomes a projection |
| #164 | Concurrent branches | Parallel execution, checkpoints through one writer |
| #165 | Compensate a graph | Rollback walks history backwards, not the sequence |
| #166 | Recover a forked instance | Resume from the stored set, matched by name |
| #171 | Shape over HTTP | `WorkflowGraph.Of`; a condition reports *that* it is one |
| #172 | Shape in the dashboard | Nested lists, not a canvas |
| #167 | Branching documented | The data hazard stated where an author will look |
| #181 | Run overlay | History drawn on the shape; closes #40 |

**Parallel branches run genuinely concurrently**, which broke the invariant
everything since M2 rested on: that one instance is executed by one worker. That
was chosen knowing the cost, so the machinery was made honest rather than the
comment left lying. Concurrency is confined to step execution — checkpoints
serialise through a single per-instance writer, because concurrent branches each
holding a stale `Revision` would have every save but one rejected.

**Found while building, not by planning:**

- **`ActiveNode` needed hand-written structural equality.** The compiler's
  version compares `BranchPath` by reference, so two nodes describing the same
  position were unequal whenever the lists were separate objects — which is
  always, once one side has been through a store. Not only a test problem:
  anything diffing a position against the one it last saw would see a change on
  every read.
- **The fork must checkpoint before starting its arms.** A crash between opening
  the fork and the first arm's first checkpoint would otherwise find an instance
  recorded at the step that forked, with no way to know it had.
- **Recovery matches on step name, not branch path.** `Fork` labels every fork's
  arms `branch-1` and `branch-2`, so two forks in one workflow produce identical
  paths. The path is for reading a position, not for finding one.
- **A limitation cited a closed issue, and gave a reason that had stopped being
  true.** #164 refused `Outcome.Suspend` inside a branch because the durable
  position was the branching step; #163 and #166 removed that reason and the
  guard outlived it. What genuinely blocks it is that nobody has decided what
  `Suspended` means while sibling branches are still running — now #179.

**Honest limitation:** only a *choice* can prove a branch was skipped. "Not
reached yet" and "on a path we skipped" look identical for most of a run, and a
fork runs every arm, so the run overlay marks nothing as not taken there.

**Carried forward:** best-effort branches have no expression at all — any branch
failure fails the instance, deliberately (#161); suspending inside a branch fails
(#179); and definitions are still C# classes registered at startup, so the
*designer* half of #40's title is a canvas nobody can draw on. That is #183, and
it is blocked on authentication (#42) before it is blocked on anything else —
authoring a definition remotely is composing code remotely.

**#40 was closed with half its title undelivered**, which is worth saying plainly
rather than letting a green milestone imply otherwise. The read-only half — shape
over HTTP, shape rendered, run drawn on it — is done. Authoring was never
decomposed because its open questions were never settled, and closing the epic
without a successor would have left four live references pointing at a closed
issue. Hence #183.

## M8 — Observability ✅

The engine was **almost entirely silent**. The only `ILogger` in the codebase was
in `DispatcherHostedService` and it said three things about polling, so NFR-2's
live half — what is happening to an instance right now — had never existed. M6
and M7 widened the gap rather than closing it.

| Issue | Story | Outcome |
| --- | --- | --- |
| #41 | Observability epic | [ADR-0025](adr/0025-observability.md) |
| #185 | Instance lifecycle logging | `EngineLog`, seven events, instance id as a scope |
| #186 | Step, retry and rollback logging | Debug for progress, above it for trouble |
| #187 | Instance outcome counters | `EngineMetrics`; one mapping, five counters |
| #188 | Instance and step tracing | `EngineTracing`; one trace from request to step |
| #189 | Scrape endpoint and OTLP export | Hand-rolled exposition; OTLP opt-in |
| #190 | Documentation and the data boundary | The guide, and the canary that keeps it true |

**Instrumentation is BCL; exporting is the host's.** `FlowDeck.Core` emits
through `ActivitySource`, `Meter` and `ILogger` and takes no OpenTelemetry
dependency. It did take its **first package reference in the project's life** —
`Microsoft.Extensions.Logging.Abstractions` — argued per case against ADR-0010
rather than assumed.

**Found while building, not by planning:**

- **The Prometheus exporter has never shipped stable.** Every release since 1.5
  is `-beta.1`. Found while writing the ADR, which changed the decision: the
  exposition endpoint is hand-rolled, and OTLP — where re-implementing a wire
  protocol would be reckless — stays a package.
- **Nothing was exported at all**, and it took standing up a real collector to
  see it. FlowDeck's instance span is a child of the ASP.NET request activity,
  the default sampler is `ParentBased`, and the request activity was not recorded
  because nothing instrumented it — so the workflow span was not recorded either.
  `AddAspNetCoreInstrumentation` is a correctness requirement here, not a nicety.
- **`OTEL_EXPORTER_OTLP_ENDPOINT` is a *base* endpoint.** The SDK appends the
  signal path only for endpoints it read from the environment itself; one set
  through `OtlpExporterOptions` is taken literally. Every export was POSTing to
  the collector's root and being 404'd.
- **A default `ActivityContext` does not force a root span.** `ActivitySource`
  reads it as "no parent specified" and falls back to `Activity.Current`, so
  recovered instances silently attached to the dispatcher poll that found them.
- **The scrape endpoint was resolved lazily**, so its `MeterListener` did not
  exist until someone first called `/metrics`. The first scrape after a deploy
  would have under-reported exactly the runs an operator was checking on.
- **A `MeterListener` is process-wide.** Matching meters by name aggregated a
  second engine's measurements into this host's series, which reads as an
  inflated count rather than as a bug.

**The data boundary is tested, not stated.** ADR-0025 forbids workflow data in
any signal — not a value, not a key, not a count. A scenario plants a canary in
workflow data, pushes it through every path the engine has including a failing
run that rolls back, and searches every log entry, span and measurement. Injecting
a deliberate leak fails it, which is the only thing that makes green mean
anything.

**Carried forward:** step duration (#198), retry and per-action compensation
counters (#199) and cluster health (#200) are all deferred with reasons recorded
rather than left off a list nobody wrote. `/metrics` is unauthenticated like the
rest of the API (#42).

## M9 — Production Hardening ✅

**Authentication was deferred.** #42 moved to a new M11 at the maintainer's
direction, so this milestone is the version lifecycle (#67) and the tenancy and
performance questions (#43). That is worth stating plainly: a milestone called
Production Hardening that ships without auth is hardening in a narrower sense
than the name suggests.

| Issue | Story | Outcome |
| --- | --- | --- |
| #67 | Version lifecycle epic | [ADR-0026](adr/0026-definition-version-lifecycle.md) |
| #43 | Tenancy and performance epic | [ADR-0027](adr/0027-multi-tenancy-and-performance.md) |
| #202 | Filter and count by version | `InstanceFilter.DefinitionVersion`, `ActiveOnly` |
| #203 | Refuse to retire a held version | `RetireAsync`; `DefinitionInUseException` carries the count |
| #204 | Two versions side by side | Characterization — no production code needed |
| #205 | Show which versions are in use | Grouped count; the workflow list stops being a placeholder |
| #206 | Throughput baseline | [Performance baseline](performance.md); a loose regression guard |
| #207 | Version lifecycle documented | Prose asserted, bounded to its own section |

**The decisions.** In-flight instances run to completion on the version they
started — no migration, no patching API — and the cost is stated rather than
implied: *a bug in a deployed version stays in that version*. Retiring a version
instances still hold is refused, and the refusal says how many. Multi-tenancy is
out of scope for v1 as a recorded decision. Performance is a measured baseline
with a loose guard, not a target.

**Found while building, not by planning:**

- **The hazard #203 closes was already live.** A host that simply stopped
  registering a version left every in-flight instance of it unresumable, because
  `ResumeAsync` and the dispatcher both resolve through the registry. Nothing
  reported it, and an operator would find out days later when a recovery failed.
- **A scenario was passing for the wrong reason.** `EngineContext` keyed
  declarations by id alone, so #203's "retiring one version leaves the others
  alone" registered only v2 — retiring v1 failed as *not found*, and the
  assertion that v2 still worked was true for unrelated reasons. Found by #204,
  which needed two versions to exist at once.
- **Widening `IWorkflowStore` costs eleven test doubles.** They implement it by
  delegating to an inner store, so a new member breaks all of them. The grouped
  count ships as a **default interface member** — correct but unoptimised, with
  both providers overriding it — because a contract nobody wants to extend is a
  worse outcome than a default that is right and slow.
- **The per-step cost assertion is the interesting half of #206.** A ten-step
  instance costs ~4 µs per step against a one-step instance's ~12 µs, because
  creation is paid once and spread. If that stops amortising, something has
  started scaling with step count — a regression the headline number would hide.

**Honest limitation:** the performance guard is ~2,700× below the measured rate.
It catches an order-of-magnitude regression and **will not catch a 2×**. That is
the trade for running on shared runners, where a flaky guard gets deleted rather
than investigated, and `performance.md` says so.

**Carried forward:** no migration for in-flight instances (#67 stays open as the
epic covering it), multi-tenancy out of scope (ADR-0027), and the API still has
no authentication (#42, now M11).

## M10 — Operator Control ✅

FlowDeck had **one** operator action: cancel. For an engine whose dashboard is
modelled on Octopus Deploy and Hangfire, that was a gap rather than a scoping
decision — and `ResumeAsync` was the sharpest symptom, a public signature that
arrived because #12 needed it to prove a clause and was never designed.

| Issue | Story | Outcome |
| --- | --- | --- |
| #66 | Operator actions epic | [ADR-0028](adr/0028-operator-actions.md) |
| #179 | Suspending inside a branch | [ADR-0029](adr/0029-suspending-inside-a-branch.md); the guard's reason had expired |
| #68 | Resume over HTTP and in the dashboard | The gap the issue was filed about, closed |
| #124 | Cancel and roll back | Two actions, not a flag — open since ADR-0021 |
| #216 | Retry from the start | New linked instance; `RetriedFromInstanceId` |
| #217 | Retry from the failing step | Reuses the crash-recovery resumption path |
| #218 | Suspend a running instance | The concurrency token is the signal |
| #219 | Bulk cancel and retry | Best-effort, per-item report, bounded |
| #220 | Operator actions documented | Prose asserted, RED confirmed |

**Retry keeps ADR-0008 intact.** Both modes create a new linked instance and
leave the original exactly as it was. Reopening a terminal instance was on the
table and was rejected: "this instance failed" is a fact, and an action that made
it retroactively untrue would rewrite the record an operator is using to decide
what to do. The cost — the instance id changes — is named in the ADR, carried by
`RetriedFromInstanceId`, and is the first thing the guide says.

**Not built, deliberately:** editing workflow data on a suspended instance. #66
calls it the most useful and most dangerous action on its list, and that is the
right reading.

**Found while building, not by planning:**

- **The branch-suspension guard had been unjustified since #166.** #163 made the
  position set-valued and #166 taught resume to use it, so the machinery already
  existed. Only the *meaning* of `Suspended` mid-fork was unsettled.
- **A parked sequence must not close its cursor.** The first attempt at #179 set
  the status at the join and checkpointed after the `finally` had closed every
  cursor, so a suspended instance was recorded as being **nowhere** — which
  recovery reads as "already finished". Caught by a failing test, not by review.
- **A mutation showed a clear was dead code.** #218's conflict handler explicitly
  cleared the suspend request; removing it failed nothing, because the running
  engine's own instance never carries the flag and every checkpoint writes
  `false` anyway. What was missing was an *assertion* on the stored record, which
  a stale flag would otherwise fail silently.
- **A bulk scenario passed for the wrong reason.** The instance meant to be
  refused was declared under a different definition id, so the filter never
  selected it — four succeeded, none failed, green. It now uses a second version
  of the same definition.
- **CI caught a wrong assertion from M9.** #206 asserted per-step cost amortises;
  it does on a fast machine and does not on a contended runner, because every
  checkpoint rewrites a record whose history keeps growing. The guard now bounds
  cost absolutely, and `performance.md` records the finding.

**Honest limitation:** there is **no audit trail**. #66 asked whether there is a
record of who did what, and there cannot be — the API has no authentication, so
an action has no subject to name. Execution history records *what* happened. This
matters most for exactly the post-incident review an operator would reach for it
in, and the guide says so.

**Carried forward:** editing workflow data (unbuilt by decision), bulk actions
capped at 200 per call, and authentication (#42, now M11 — which multi-tenancy
and any audit trail both wait on).

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

Both entries here are now closed, kept because what unblocked them is worth
remembering.

| Blocker | Resolution |
| --- | --- |
| ~~No self-hosted runner registered~~ | CI moved to GitHub-hosted runners. Every run had been queueing indefinitely, so the pipeline had verified nothing up to that point — all results were from local runs. CD still targets a self-hosted runner, which is the part that genuinely needs one. |
| ~~CodeQL detected `languages: []`~~ | Configured before any code existed. A second, conflicting CodeQL workflow was deleted; the default setup now analyses C#, TypeScript and Actions. |

## Process debt

Recorded so it is not repeated.

| Item | Fix |
| --- | --- |
| The Phase 1 breakdown created 43 issues, none for documentation | #58; future milestones include documentation stories |
| ADRs were written retrospectively after M1 | Future ADRs ship in the same PR as the change |
| Frontend milestones have no stories for accessibility or i18n | ✅ Closed by #62: ADR-0016, ADR-0017, ADR-0018 written before M4 started |
| Four product areas were under-covered or mis-filed - found by review, not by planning | #66, #67, #68 and a scope question on #38 |
| Decisions made in an ADR but tested by nothing — found by mutation testing in M5 and M6, not by a failing test | Mutation testing is now run on the milestone's riskiest paths before the PR |
| A limitation pointed at a closed issue, with a reason that had stopped being true | Found while writing #167. Documented limits name a **live** issue, and the reason is re-read when the code it describes changes |

## Definition of Done

A story is done when: BDD scenarios exist as tests; those tests failed before
implementation, or the PR explains why they could not; `dotnet test` and
`dotnet format --verify-no-changes` pass; non-obvious decisions have an ADR;
and it merged via a PR closing the issue.
