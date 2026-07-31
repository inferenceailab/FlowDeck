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
