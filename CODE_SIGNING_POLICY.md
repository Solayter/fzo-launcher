# Code signing policy

## Current status

No Authenticode signing provider has approved this project yet. No sponsorship
or certificate from SignPath is claimed. Update catalog signing uses our own
RSA key and is not Windows Authenticode.

## Proposed SignPath process

* Repository owner, author/reviewer and release approver: GitHub user Solayter.
* Build and test on a GitHub-hosted Windows runner from a reviewed commit.
* Keep the CI token read-only; never run untrusted PR code with secrets.
* Require a manual release approval before submitting a verified artifact for
  Authenticode signing. No blanket approval of future builds.
* Verify the signed EXE and recompute hashes before publishing.
* Do not distribute signing keys to contributors or to the game server.

Only after SignPath approval, replace the status above with the actual policy
and their required attribution: "Free code signing provided by SignPath.io,
certificate by SignPath Foundation", with links to the providers. Until that
approval, this sentence describes their required future wording, not a claim
that this project already receives their service.

See [PRIVACY.md](PRIVACY.md). All maintainers involved in signing must enable
multi-factor authentication, as required by the provider. Repository/SignPath
settings and approvals must be verified by the owner during enrollment.
