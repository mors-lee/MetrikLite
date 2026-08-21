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

MetrikLite reads local preferences and Codex rate-limit responses. It does not read conversations, project files, browser storage, GitHub credentials, or Codex credential-file contents. Update checks are user initiated and use only public GitHub Release metadata.
