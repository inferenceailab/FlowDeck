# Deployment

FlowDeck deploys to a self-managed homelab via GitHub Actions running on a
**self-hosted runner**. Nothing in this pipeline uses GitHub-hosted compute.

## Topology assumption

The CD workflow assumes **the self-hosted runner and the deployment target are
the same machine** (or that the runner can reach the Docker socket of the
target). Deployment is therefore a local `docker compose up`, and no SSH
credentials are stored in GitHub.

If the runner is *not* in the homelab, replace the "Deploy with docker compose"
step in `.github/workflows/cd.yml` with an SSH invocation and add a deploy key
to the `homelab` environment. Prefer moving the runner over adding the key.

## One-time setup

1. **Register the self-hosted runner** on the homelab machine:
   Settings → Actions → Runners → New self-hosted runner.
   Until this exists, every job queues indefinitely.

2. **Runner prerequisites:** Docker with the compose plugin, `curl`, `git`,
   `gitleaks`, the .NET 10 SDK and Node 24. The CI secret-scan job fails
   deliberately if `gitleaks` is missing rather than skipping silently.

3. **Create the `homelab` environment:** Settings → Environments → New.
   Add required reviewers to gate every deployment behind an approval.

4. **Environment variables** (`vars`, not secrets):

   | Name | Example | Used by |
   | --- | --- | --- |
   | `HOMELAB_URL` | `https://flowdeck.homelab.lan` | deployment link in the UI |
   | `HOMELAB_API_URL` | `http://localhost:8080` | readiness probe |

5. **Create `deploy/homelab/.env`** on the host from `.env.example` and fill in
   the database credentials. It is excluded by `.gitignore`; never commit it.

No registry secret is needed — GHCR authentication uses the workflow's own
`GITHUB_TOKEN`.

## What runs where

| Workflow | Trigger | Jobs |
| --- | --- | --- |
| `ci.yml` | PR into `main`, push to `main` | backend, frontend, secret scan, gate |
| `cd.yml` | push to `main`, manual dispatch | build and push images, deploy |

`cd.yml` never runs on a pull request, so a fork or untrusted branch cannot
reach the homelab.

## Rollback

The deploy job records the running image tag before changing anything. If the
readiness probe fails, it restores that tag and still reports failure. On a
first-ever deployment there is nothing to roll back to, so the stack is left
running for inspection and the job fails loudly.

Manual rollback:

```sh
cd deploy/homelab
IMAGE_TAG=<previous-sha> docker compose up -d
```

## Container hardening

Both application containers run `read_only` with `no-new-privileges`, writable
paths limited to tmpfs. Postgres is not published to the host — only the `api`
service can reach it over the compose network.
