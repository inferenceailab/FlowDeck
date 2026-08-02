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
| `flowdeck_steps_duration_seconds` | histogram | `definition_id`, `definition_version`, `step_name`, `outcome` |
| `flowdeck_instances_executing` | gauge | *(none)* |
| `flowdeck_instances_recovered_total` | counter | `node_id` |

`flowdeck_steps_duration_seconds` records **one observation per execution**, so a
step retried three times contributes three — averaging them into one would hide
that it took three goes. It is tagged with the outcome, because a step that fails
fast and a step that succeeds slowly are different problems and should not share
a series.

Seconds, not milliseconds: Prometheus and the OpenTelemetry conventions both use
base units, and a histogram named in the wrong one is a dashboard nobody can
compare against anything else. The engine's *logs* stay in milliseconds, where a
human reads them.

Bucket edges are `0.001 0.005 0.01 0.05 0.1 0.5 1 5 10 30` seconds. Anything
slower lands in `+Inf` and is still counted — a step waiting on something slow is
exactly the case worth finding.

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

### Cluster health is per node, not per cluster

`flowdeck_instances_executing` is what **this node** is running right now, and
`flowdeck_instances_recovered_total` is what this node picked up after another
node stopped. Sum them across nodes to get the cluster.

That is a decision, not a limitation. A node could query the store for a
cluster-wide figure, and it would put database load on every scrape from every
node and report the same number N times — which is exactly wrong, because
summing across nodes is what a query language does by default. Each process
exports its own state and the query aggregates; that is the Prometheus model
rather than a compromise with it.

**Leases held is not a separate metric.** A claim is held only while the run is
in flight, so it would be `flowdeck_instances_executing` under a second name —
and two names for one quantity is how a dashboard comes to disagree with itself.

**`flowdeck_instances_recovered_total` is the one to alert on.** A node quietly
recovering work every few minutes means another node is dying repeatedly, and
nothing else surfaces that: the recovery *is* the system working, so there is no
failure anywhere for anyone to notice.

### What is deliberately not measured

Everything ADR-0025 deferred has since been built — step duration (#198), retry
and compensation counts (#199) and cluster health (#200). Nothing on that list
remains.

What is still absent is narrower and follows from decisions elsewhere: nothing
distinguishes a fork's arms, because parallel branches share their instance's
tags; and no metric carries an instance id, because that is unbounded
cardinality and belongs on a log and a span, where it already is.

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
| Nothing distinguishes a fork's arms in metrics | — |
| Cluster metrics are per node and must be summed | — |

## See also

- [ADR-0025](../adr/0025-observability.md) — why each of these is shaped this way
- [Defining a workflow](defining-a-workflow.md) — for workflow authors
- [Architecture](../architecture.md)
