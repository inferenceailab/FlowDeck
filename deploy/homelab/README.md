# Deploying FlowDeck to the homelab

```bash
docker compose pull && docker compose up -d
```

CD does exactly this, on a self-hosted runner that lives **on the homelab box**.
That is why no SSH key is stored in GitHub secrets and no inbound port is open:
the runner reaches out to GitHub, and the deployment is local.

## Images

Built and pushed by CD, never built here. A deployment that compiled from source
would deploy whatever the box happened to have checked out rather than the
artefact CI verified.

| Variable | Default | Meaning |
| --- | --- | --- |
| `IMAGE_PREFIX` | `ghcr.io/inferenceailab/flowdeck` | Registry and repository prefix |
| `IMAGE_TAG` | `latest` | Usually the commit SHA CD built |
| `WEB_PORT` | `8080` | Host port the dashboard listens on |
| `NODE_ID` | `homelab` | This node's identity while it holds a lease |
| `OTLP_ENDPOINT` | *(empty)* | Where to send traces. Empty means tracing is not wired at all |
| `OTLP_PROTOCOL` | `grpc` | `grpc` or `http/protobuf` |

## Observability

**Metrics need no configuration and no extra container.** The API serves
`/metrics` in Prometheus text exposition format, always. A Prometheus that
already exists can scrape it; one that does not is not a reason for FlowDeck to
count nothing.

**Tracing is opt-in**, because there is nothing sensible to do with a trace when
there is nowhere to send it. Leave `OTLP_ENDPOINT` empty and the pipeline is not
built — no exporter, no retries, no logs about a collector nobody asked for. Set
it and the API exports its request spans together with FlowDeck's instance and
step spans, so a slow endpoint and the step responsible appear in one trace.

Neither carries workflow data: ids, names, statuses and durations only. A span
leaves the process for a backend that may be third-party, so this is a boundary
rather than a default ([ADR-0025](../../docs/adr/0025-observability.md)).

`/metrics` is unauthenticated, like the rest of the API (#42). It exposes
definition ids and counts.

## Why `NODE_ID` is set

`ClusterOptions` defaults to `machine:process`, which in a container is a
container id that changes on every restart. That is *correct* — a restarted
process must not inherit its predecessor's leases (ADR-0023) — but it makes the
dashboard's "Running on" field unreadable.

Naming the node keeps it legible. It stays honest because the lease still
lapses on its own if the container dies without draining.

## Health

The API container reports **readiness**, not liveness: a node whose store is
unreachable is running correctly and cannot serve, so it should leave rotation
rather than be restarted. Restarting it during a database outage produces a
restart loop across every node and makes recovery harder.

`web` depends on `api` for **ordering only**. nginx proxies `/api` and returns
502 until the API answers; waiting for healthy would make the dashboard
unreachable during an API outage, which is exactly when an operator wants it.

## What this does not do yet

The API defaults to the **in-memory store**, so instances do not survive a
restart of the `api` container. Pointing it at PostgreSQL or SQL Server is the
remaining step, and it needs a connection string this file deliberately does not
invent.
