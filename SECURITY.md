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
| Force-push / deletion of `main` | blocked |
| Pull request required to merge to `main` | required |

Code scanning (CodeQL), secret scanning and push protection are not available
on this plan for private repositories. If this repository is made public, or
GitHub Code Security / Secret Protection is purchased, enable them.

## Dependency Hygiene

- Pin GitHub Actions to a full commit SHA, never a floating tag.
- Do not commit secrets. Use repository or environment secrets.
- Review Dependabot pull requests before merging; they are not auto-merged.
