# Observing FlowDeck

For whoever runs FlowDeck. If you are writing workflows, you want
[Defining a workflow](defining-a-workflow.md) instead.

FlowDeck emits three things: **structured logs**, **metrics** and **traces**. It
emits them through the BCL — `ILogger`, `Meter` and `ActivitySource` — and takes
no OpenTelemetry dependency in the engine itself. Exporting is the host's job
([ADR-0025](../adr/0025-observability.md)).

## No workflow data is emitted, ever

Not a value, not a key, not a count of keys. Logs, spans and metric tags carry
only: instance id, definition id and version, step name, branch, status, attempt
number, duration, node id, and the type and message of an exception the engine
already records.

This is stricter than what FlowDeck *persists*, deliberately. A store is yours;
a span leaves the process for a backend that may be third-party, retained on
someone else's schedule and searchable by people who never had database access.
A secret that reaches a trace is a secret in a vendor's index.

Keys were considered and rejected along with values: key names are author-chosen,
and `customer-ssn` is a disclosure on its own.

**This is asserted, not merely intended.** A scenario plants a canary value in
workflow data, runs an instance with logging, metrics and tracing all captured,
and fails if that value appears anywhere in any of them.

## Metrics

Served at `GET /metrics` in Prometheus text exposition format. Always on — no
configuration, no extra container. A Prometheus that already exists can scrape
it.

| Metric | Type | Labels |
| --- | --- | --- |
| `flowdeck_instances_started_total` | counter | `definition_id`, `definition_version` |
| `flowdeck_instances_completed_total` | counter | `definition_id`, `definition_version` |
| `flowdeck_instances_failed_total` | counter | `definition_id`, `definition_version` |
| `flowdeck_instances_cancelled_total` | counter | `definition_id`, `definition_version` |
| `flowdeck_instances_compensated_total` | counter | `definition_id`, `definition_version`, `outcome` |
| `flowdeck_steps_retried_total` | counter | `definition_id`, `definition_version`, `step_name` |
| `flowdeck_compensations_total` | counter | `definition_id`, `definition_version`, `step_name`, `outcome` |

`flowdeck_steps_retried_total` counts attempts **beyond the first**, so an
ordinary run contributes nothing and the number reads as "how much trouble is
this having" rather than "how much work is it doing". It is tagged by step,
because *which* step is retrying is the part you cannot get anywhere else
without reading history per instance.

`flowdeck_compensations_total` is per **action**, where
`flowdeck_instances_compensated_total` is per instance. The instance counter says
a rollback happened and how it ended; this says how much of it succeeded — for a
partial rollback, the difference between "one undo failed" and "nine did".

An instance that rolled back is counted as **compensated and not as failed**, so
the two never double-count one incident. The `outcome` label separates a clean
rollback from `CompensationFailed`, which is the outcome that always needs a
human.

Labels carry no per-instance value. That is a cardinality rule as much as a
disclosure one: a label that varied per run would give a time-series backend one
series per instance, which is how a metrics pipeline is brought down rather than
how it is used.

`/metrics` is unauthenticated, like the rest of the API (#42).

### What is deliberately not measured

- **Step duration** (#198). The question an operator usually arrives with is
  "which step is slow", and the answer today is in execution history per instance
  rather than aggregated. The data exists; the aggregate does not.
- **Cluster health** (#200) — instances running, leases held, recoveries
  performed. M6's machinery is inferred from the dashboard rather than measured.

These are scope, not oversight, and each is tracked.

## Traces

Exported over OTLP **only when an endpoint is configured**. Leave it unset and no
pipeline is built at all — nothing retries into the void, and nothing logs about
a collector you never asked for.

| Variable | Meaning |
| --- | --- |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Where to send traces. Unset means tracing is off |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` (default) or `http/protobuf` |

Two spans:

| Span | Attributes |
| --- | --- |
| `workflow.instance` | `workflow.instance.id`, `workflow.definition.id`, `workflow.definition.version` |
| `workflow.step` | `workflow.step.name`, `workflow.step.attempt`, `workflow.branch` |

`workflow.branch` is present only on a step inside a branch. A step on the
top-level sequence carries no branch rather than an empty one.

A failing span is marked with `ActivityStatusCode.Error` and an `error.type`
attribute. The exception's **type and message**, never the exception object — a
stack trace and whatever an author's message interpolated are exactly what the
rule above keeps out.

### One trace, from request to step

An instance started over HTTP runs inline on the request thread, so its span is a
child of the request's and a slow endpoint appears in the same trace as the step
responsible for it. The API instruments ASP.NET Core for the same reason: the
default sampler is `ParentBased`, so an unrecorded request span means the
workflow spans below it are not recorded either.

**A recovered instance is a root.** An instance a dispatcher picks up after a
crash has no caller, and hanging its run off the poll that found it would put the
wrong cause in the trace.

**A retried step opens a span per attempt**, not per step. Three attempts are
three spans, which is what makes a struggling step visible instead of averaged
into one.

## Logs

Enable the `FlowDeck.Core` category. The instance id, definition id and version
arrive on every entry through an `ILogger` **scope**, so a structured sink can
group a run without each message repeating them.

| Event | Level | When |
| --- | --- | --- |
| `InstanceStarted` | Information | An instance was created and is about to run |
| `InstanceResumed` | Information | A suspended instance was picked up again |
| `InstanceSuspended` | Information | A step asked to be resumed later |
| `InstanceCompleted` | Information | Every step ran |
| `InstanceCancelled` | Information | An operator stopped it |
| `InstanceFailed` | **Error** | A step failed and retries were exhausted |
| `InstanceCompensated` | Warning | A rollback ran; `Status` says how it ended |
| `StepStarted` | Debug | A step is about to execute |
| `StepFinished` | Debug | With outcome, duration and attempt |
| `StepRetrying` | Warning | With the attempt number and the delay |
| `StepRolledBack` | Information | A compensating action undid a step |
| `RollbackFailed` | **Error** | A compensating action failed; its effects remain |

### The levels are the design

Step detail is Debug and everything abnormal is above it, so the default is
**quiet while a workflow is healthy and loud the moment it is not**. A twenty-step
workflow at Information would emit forty entries per instance and bury the six
that describe the run as a whole. Turn `FlowDeck.Core` down to Debug when you
want the play-by-play.

`StepRetrying` carries the delay, which is the difference between a workflow
backing off for thirty seconds and one that has hung — identical from outside
otherwise.

A rollback gets its own events rather than appearing as a step named
`compensate:x`. That prefix is how one history table keeps two kinds of row
apart; it is not something a log reader should have to parse.

## Known limitations

| Limitation | Tracked by |
| --- | --- |
| `/metrics` is unauthenticated | #42 |
| No step-duration metric | #198 |
| No cluster health metrics | #200 |
| Nothing distinguishes a fork's arms in metrics | — |

## See also

- [ADR-0025](../adr/0025-observability.md) — why each of these is shaped this way
- [Defining a workflow](defining-a-workflow.md) — for workflow authors
- [Architecture](../architecture.md)
