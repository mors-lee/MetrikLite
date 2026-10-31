# Security Policy

## Supported versions

Security fixes are provided for the latest published MetrikLite release.

## Reporting a vulnerability

Please use GitHub's private vulnerability reporting feature when it is available for this repository. If private reporting is unavailable, open an issue containing no secrets or exploit details and ask the maintainer for a private contact channel.

Never include Codex credentials, GitHub tokens, cookies, account identifiers, or private source code in a report.

## Release provenance and verification

- Release binaries are produced by `.github/workflows/release.yml` from a public `v*` tag.
- Local manually compiled binaries are not accepted as official Release attachments.
- `SHA256SUMS.txt` contains release-asset checksums.
- `MetrikLite-SBOM.spdx.json` lists packaged dependencies.
- Dependabot, CodeQL, `cargo audit`, and `npm audit` run in the repository.

Public builds are currently not Authenticode-signed. SmartScreen reputation, code signing, source review, checksums, and antivirus scans are complementary signals; none is absolute proof that software is safe.

## Data boundaries

MetrikLite reads local preferences and bounded headers/tails of desktop session logs to identify Codex Desktop sources and extract quota events. It does not parse, display, or upload conversation bodies. It does not start a Codex CLI, fall back to CLI authentication, or access API keys, `auth.json`, OS credential stores, project files, browser storage, or GitHub credentials. Cache timestamps are the original desktop record times, not polling times. Update checks are user initiated and use only public GitHub Release metadata.
