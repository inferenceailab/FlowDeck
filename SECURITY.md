# Security Policy

## Reporting a Vulnerability

This is a private repository. Report suspected vulnerabilities directly to the
repository owner rather than opening a public issue. Please include:

- affected component and version / commit SHA
- reproduction steps or proof of concept
- assessed impact

Expect an acknowledgement within 5 business days.

## Supported Versions

Only the default branch (`main`) receives security fixes.

## Enabled Protections

| Control | Status |
| --- | --- |
| Dependency graph | enabled |
| Dependabot alerts | enabled |
| Dependabot security updates | enabled |
| Dependabot version updates | enabled (`.github/dependabot.yml`) |
| `GITHUB_TOKEN` default permissions | read-only |
| Actions approving pull requests | blocked |
| Allowed Actions | GitHub-owned and verified creators only |
| Actions must be SHA-pinned | required |
| Web commit sign-off | required |
| Signed commits | all commits signed with SSH key, verified by GitHub |
| Local secret scanning | `gitleaks` pre-commit hook (`.githooks/pre-commit`) |

## Setting Up a Clone

`core.hooksPath` is local config and is not carried by `git clone`. After
cloning, run:

```sh
git config core.hooksPath .githooks
scoop install gitleaks   # or see https://github.com/gitleaks/gitleaks#installing
```

The hook refuses to run if `gitleaks` is missing, rather than silently letting
unscanned commits through. Bypass with `git commit --no-verify` only if you are
certain; if a secret ever reaches a commit object, rotate it — amending does not
remove it from the reflog.

Note: the default gitleaks ruleset does **not** flag a bare AWS access key ID
(`AKIA…`). It does catch GitHub PATs, Slack tokens, and private keys. Add
custom rules in `.gitleaks.toml` if the project starts handling AWS
credentials.

## Not Currently Available

These controls are unavailable while this repository is **private on a free
personal plan**. Making it public, or upgrading to GitHub Pro, enables them.

| Control | Blocked by |
| --- | --- |
| Branch protection / rulesets on `main` | needs GitHub Pro, or public |
| Required pull request and review before merge | needs GitHub Pro, or public |
| Blocking force-push and branch deletion | needs GitHub Pro, or public |
| Required signed commits | needs GitHub Pro, or public |
| Code scanning (CodeQL) | needs GitHub Code Security, or public |
| Secret scanning and push protection | needs GitHub Secret Protection, or public |
| Private vulnerability reporting | public repositories only |

## Dependency Hygiene

- Pin GitHub Actions to a full commit SHA, never a floating tag.
- Do not commit secrets. Use repository or environment secrets.
- Review Dependabot pull requests before merging; they are not auto-merged.
