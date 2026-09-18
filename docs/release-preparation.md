# Linux arm64 release preparation

The deployment target is Linux arm64. The [candidate record](deployment-candidate.json) identifies the clean verified source, local image manifest digest, build-input fingerprint, scanner report, and test evidence. Merged main has the same 96 image build inputs and passed [its complete CI run](https://github.com/mikearmstrong1/QaEngineerAgent/actions/runs/35351744151). CI verifies on push but does not publish an image; its runner-built image is separate from the local arm64 candidate.

## Prepared artifact

- Local source: `quality-system@sha256:cabc4f2b72668b79950187cf598d774c1700a78d282a1058cb9a548250430971`
- Target platform: `linux/arm64`
- Prepared registry tag: `ghcr.io/mikearmstrong1/qaengineeragent:verified-6a6f7ea-arm64`
- Publication and deployment: pending authorization

Run `bash scripts/publish-verified-arm64.sh --check` to recheck the local manifest, platform, and all recorded build inputs without contacting the registry. The script's `--publish` mode requires a GHCR login, refuses to overwrite an existing tag, pushes the exact local image, compares the pushed manifest digest to the candidate digest, and prints a registry-qualified immutable reference. A failed digest check stops deployment use even if the push completed.

For a local GHCR login, [GitHub documents](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry#authenticating-with-a-personal-access-token-classic) a personal access token (classic) with `write:packages` and `docker login ghcr.io -u mikearmstrong1 --password-stdin`. Keep the token outside the repository. Registry access is not yet configured on this host; a read-only lookup returned `denied`. A command-line push may create a package that is not automatically linked to the repository, so check package access after publication.

After an authorized publication, record the registry-qualified `ghcr.io/...@sha256:...` reference in the candidate record. Verify the remote manifest and pull it by digest on an arm64 host before deployment. The existing `quality-system` Compose stack remains running until deployment is separately authorized.
