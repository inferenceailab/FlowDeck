# Security Policy

## Reporting a Vulnerability

**Do not open a public issue for a security vulnerability.**

Use GitHub's [private vulnerability reporting][pvr] — the **Report a
vulnerability** button under the Security tab. It is enabled on this
repository. Please include:

- affected component and version / commit SHA
- reproduction steps or proof of concept
- assessed impact

Expect an acknowledgement within 5 business days.

[pvr]: https://github.com/inferenceailab/FlowDeck/security/advisories/new

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
| Signed commits | required on `main`, no bypass |
| Secret scanning | enabled |
| Secret scanning push protection | enabled |
| Private vulnerability reporting | enabled |
| Code scanning (CodeQL) | default setup configured |
| Force-push to `main` | **blocked, no bypass** |
| Deletion of `main` | **blocked, no bypass** |
| Linear history on `main` | required, no bypass |
| Pull request before merge | required (repo admin may bypass) |
| Local secret scanning | `gitleaks` pre-commit hook (`.githooks/pre-commit`) |

Two rulesets protect `main`, deliberately split:

- **`main-protection`** — deletion, force-push, non-linear history and unsigned
  commits are refused **for everyone, including repository admins**. There is no
  bypass actor, so destroying history requires consciously disabling the
  ruleset first.
- **`main-pull-request`** — requires a pull request with one approving review,
  but repo admins may bypass. Without this split, a solo maintainer could never
  merge anything, since you cannot approve your own pull request.

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

## Known Gaps

| Gap | Detail |
| --- | --- |
| CodeQL analyses no languages yet | Default setup is configured but detected `languages: []` — there is no application code in the repository yet. Re-check once the first real source lands. |
| Secret scanning non-provider patterns | Reported `disabled`; the API accepts the change but does not apply it. Toggle under Settings → Code security if wanted. |
| Secret scanning validity checks | Same as above. |
| gitleaks AWS coverage | The default ruleset does not flag a bare AWS access key ID. |

## Dependency Hygiene

- Pin GitHub Actions to a full commit SHA, never a floating tag.
- Do not commit secrets. Use repository or environment secrets.
- Review Dependabot pull requests before merging; they are not auto-merged.
