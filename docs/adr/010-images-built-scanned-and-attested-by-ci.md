# ADR-010: Images are built only by CI, scanned before they are published, and attested

- **Status:** Accepted
- **Date:** 2026-11-10
- **Decision ids:** none

## Context

CheapHours runs as one image in three roles (ADR-001). Whatever deploys it (the Helm chart, later) needs an
answer to "is the image I am about to run the code that was reviewed and tested, and is it safe to run?" Until
now there was no image at all: the roles ran through `dotnet run` on a developer's Windows machine, and nothing
proved that the code on `main` built and passed on Linux.

Constraints: the repository is public, so GitHub-hosted runners, GHCR, code scanning and artifact attestations
cost nothing; pull requests may come from forks and must never see a write token; no long-lived secret should be
needed to publish; the image must run on amd64 and arm64 nodes; `Europe/Berlin` must resolve inside the container.

## Decision

- **Only CI publishes.** The image is pushed to `ghcr.io/shakerkamal/cheaphours` by the `publish` job of the CI
  workflow, for pushes to `main` and `v*` tags only, and only after every other job of the same run (lint, the whole
  test suite on Linux, the dashboard tests, the image checks) passed. That job is the only one with write scopes,
  and it logs in with the run's `GITHUB_TOKEN`.
- **Scanned before it is published.** The `image` job builds the amd64 image, runs the smoke checks
  (`scripts/ci/image-smoke.sh`: liveness without a database, an unknown role exits 2, a non-root user, Berlin
  time-zone data, no local secrets), and scans it with Trivy; a high or critical vulnerability with a fixed version
  fails the run. The results appear in code scanning. `publish` rebuilds both platforms from the same build cache,
  so the amd64 layers are the ones just checked.
- **Attested, not signed with a key.** Each pushed image carries an SBOM, BuildKit provenance and a provenance
  attestation signed through Sigstore with the run's OIDC identity (`actions/attest-build-provenance`). Anyone can
  check it with `gh attestation verify oci://ghcr.io/shakerkamal/cheaphours:<tag> --owner shakerkamal`.
- **Immutable tags.** Every image is tagged `sha-<short commit>`, which is what a deployment pins. `main` moves with
  each green push to `main`; a tag `vX.Y.Z` adds `X.Y.Z` and `X.Y`. There is no `latest`.
- **Small and non-root.** The runtime base is Ubuntu chiseled with time-zone data (`aspnet:…-noble-chiseled-extra`):
  no shell, no package manager, user 1654. The application is framework-dependent IL, built once and copied into
  both platforms' images.

## Alternatives considered

- **Publishing from a developer's machine.** Rejected: nothing would tie the image to a reviewed commit or a green
  test run, and the registry credential would live on a laptop.
- **Signing with a key (`cosign sign --key`).** Rejected: a private key becomes a repository secret to store,
  rotate and protect from pull-request code; keyless attestation ties the signature to the workflow and commit
  instead, with no secret at all.
- **A `latest` tag.** Rejected: it moves under running deployments, so two pods can run different code under the
  same name and a rollback has nothing stable to return to.
- **Exporting the scanned image and pushing those exact bytes (`crane`, `skopeo`).** Rejected for now: it adds a
  tool and still needs a separate arm64 build; with a warm build cache the pushed amd64 layers are the scanned ones.

## Consequences

- A deployment can pin `sha-<commit>` and verify where the image came from without trusting the registry.
- A fixable high or critical CVE in the base image blocks publishing until the base is updated; Dependabot proposes
  base-image updates weekly.
- The arm64 image is neither started nor scanned in CI; it shares the base image release and the IL with the
  checked amd64 image. Worth adding an arm64 run if the deployment targets arm64 nodes.
- If the build cache is evicted between the `image` and `publish` jobs, the pushed amd64 image is rebuilt from the
  same pinned inputs rather than reused byte for byte.
