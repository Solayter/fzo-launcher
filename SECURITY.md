# Security policy

Supported release: 1.1.x. Upgrade from 1.0.x to enable authenticated updates.

Do not publish exploit details, tokens, private keys, user data or full private
logs in public issues. Contact the maintainer through a private GitHub channel
if available; otherwise open an issue requesting a private contact without
including vulnerability details. A dedicated private reporting channel should
be enabled in repository settings before accepting sensitive reports.

## Trust boundaries

The update key's public portion is embedded in source and the EXE. It is safe
to disclose. A signature does not encrypt the public catalog or game files.

The private signing key must remain outside the website, source checkout, CI
artifacts, logs and repository history. It is protected with Windows DPAPI for
the publisher's Windows user, plus a restricted directory ACL. This protects
against a website-file-only compromise. Compromise of the signing Windows user
can still permit signing malicious updates. For stronger separation, run the
signer on a separate/offline publishing workstation and transfer only the
signed catalog. Keep a tested, secure recovery plan for the key: loss of the
key requires distributing a launcher with a replacement trust anchor.

The launcher rejects changed signatures, unknown keys, expired catalogs, path
traversal, symbolic links/junctions in client paths, incorrect manifest hashes,
and downloaded files with incorrect length or hash. Updates are staged and
rolled back after a failed/interrupted commit. These checks are not a sandbox:
authenticated game binaries execute with the user's rights.

The cache prevents rollback after observing a newer sequence. Deleting the
cache removes that local memory; a first launch relies on signed expiry.
System clock correctness is required. Every catalog is valid at most 180 days.

## CI and publishing

Use GitHub-hosted Windows runners. Never attach a runner on the game server to
this public repository. Do not give CI deployment access or the update private
key. Use 2FA, protect main and release tags, review changes to workflows and
build scripts, and require manual approval for any Authenticode signing.
The checked-in workflow builds and tests only; it cannot deploy to the server
and has no signing secrets.

Authenticode signing, if approved by a provider, must run on verified artifacts
from an approved commit. Recompute release hashes **after** signing. A signature
does not guarantee absence of browser/SmartScreen reputation warnings.
