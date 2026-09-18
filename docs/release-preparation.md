# Linux arm64 release preparation

The deployment target is Linux arm64. The [candidate record](deployment-candidate.json) identifies the clean verified source, local image manifest digest, build-input fingerprint, scanner report, and test evidence. Merged main has the same 96 image build inputs and passed [its complete CI run](https://github.com/mikearmstrong1/QaEngineerAgent/actions/runs/35351744151). CI verifies on push but does not publish an image; its runner-built image is separate from the local arm64 candidate.

## Prepared artifact

- Local source: `quality-system@sha256:cabc4f2b72668b79950187cf598d774c1700a78d282a1058cb9a548250430971`
- Target platform: `linux/arm64`
- Published registry tag: `ghcr.io/mikearmstrong1/qaengineeragent:verified-6a6f7ea-arm64`
- Immutable deployment reference: `ghcr.io/mikearmstrong1/qaengineeragent@sha256:cabc4f2b72668b79950187cf598d774c1700a78d282a1058cb9a548250430971`
- Publication: complete; deployment: pending separate authorization

Before publication, `bash scripts/publish-verified-arm64.sh --check` rechecked the local manifest, platform, and all recorded build inputs. The script's `--publish` mode refused to overwrite an existing tag, pushed the exact local image, compared the pushed manifest digest to the candidate digest, and printed the registry-qualified immutable reference. The published OCI index contains Linux arm64 and a Docker attestation manifest with an unknown platform. A pull by immutable digest with `--platform linux/arm64` passed.

For a local GHCR login, [GitHub documents](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry#authenticating-with-a-personal-access-token-classic) a personal access token (classic) with `write:packages` and `docker login ghcr.io -u mikearmstrong1 --password-stdin`. Keep the token outside the repository. A command-line push may create a package that is not automatically linked to the repository; package visibility and access should be checked before relying on an unauthenticated pull.

The [candidate record](deployment-candidate.json) now contains the registry-qualified immutable reference. The existing `quality-system` Compose stack remains running until deployment is separately authorized.
