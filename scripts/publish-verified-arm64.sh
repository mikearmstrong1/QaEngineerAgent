#!/usr/bin/env bash
set -euo pipefail

mode="${1:---check}"
if [[ "$mode" != "--check" && "$mode" != "--publish" ]]; then
  echo "Usage: $0 [--check|--publish]" >&2
  exit 2
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
candidate="$repo_root/docs/deployment-candidate.json"

# Verify the current checkout still has the exact build inputs recorded for the
# local candidate. Documentation and release scripts are outside the image.
python3 - "$candidate" "$repo_root" <<'PY'
import hashlib
import json
import subprocess
import sys
from pathlib import Path

record = json.loads(Path(sys.argv[1]).read_text())
root = Path(sys.argv[2])
include = {'.dockerignore', 'Directory.Build.props', 'Dockerfile',
           'QualitySystem.sln', 'global.json', 'package.json', 'package-lock.json'}
tracked = subprocess.check_output(['git', 'ls-files'], cwd=root, text=True).splitlines()
paths = sorted(path for path in tracked if path in include or
               path.startswith(('src/', 'tests/', 'prompts/', 'schemas/', 'playwright/')))
manifest = [[path, hashlib.sha256((root / path).read_bytes()).hexdigest()] for path in paths]
fingerprint = hashlib.sha256(json.dumps(manifest, separators=(',', ':')).encode()).hexdigest()
if len(manifest) != record['buildInputFiles'] or fingerprint != record['buildInputFingerprintSha256']:
    raise SystemExit('Current build inputs differ from the verified candidate')
if record['platform'] != 'linux/arm64' or record['published'] or record['deployed']:
    raise SystemExit('Candidate is not an unpublished Linux arm64 image')
print(f"Verified {len(manifest)} build inputs: {fingerprint}")
PY

candidate_digest="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["localImageDigest"])' "$candidate")"
source_head="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["sourceHead"])' "$candidate")"
local_ref="quality-system@$candidate_digest"
registry_repo="ghcr.io/mikearmstrong1/qaengineeragent"
target_tag="$registry_repo:verified-${source_head:0:7}-arm64"

# Looking up the image by its manifest digest establishes identity. Docker's
# displayed image ID can be a config digest on some installations.
docker image inspect "$local_ref" >/dev/null
platform="$(docker image inspect "$local_ref" --format '{{.Os}}/{{.Architecture}}')"
if [[ "$platform" != "linux/arm64" ]]; then
  echo "Local image architecture does not match the candidate" >&2
  exit 1
fi
echo "Candidate: $local_ref ($platform)"
echo "Registry tag: $target_tag"

if [[ "$mode" == "--check" ]]; then
  exit 0
fi

# Require a registry login before publishing, and refuse to overwrite a tag.
manifest_error="$(mktemp)"
trap 'rm -f "$manifest_error"' EXIT
if docker manifest inspect "$target_tag" >/dev/null 2>"$manifest_error"; then
  echo "Registry tag already exists; refusing to overwrite it" >&2
  exit 1
elif ! grep -Eiq 'manifest unknown|no such manifest' "$manifest_error"; then
  echo "Cannot confirm that the registry tag is unused; log in to GHCR first" >&2
  exit 1
fi

docker tag "$local_ref" "$target_tag"
docker push "$target_tag"
remote_manifest="$(mktemp)"
trap 'rm -f "$manifest_error" "$remote_manifest"' EXIT
docker buildx imagetools inspect "$target_tag" --format '{{json .Manifest}}' >"$remote_manifest"
python3 - "$remote_manifest" "$candidate_digest" <<'PY'
import json
import sys
from pathlib import Path

manifest = json.loads(Path(sys.argv[1]).read_text())
expected = sys.argv[2]
platforms = [f"{item['platform']['os']}/{item['platform']['architecture']}"
             for item in manifest.get('manifests', [])
             if item.get('platform', {}).get('os') != 'unknown']
if manifest.get('digest') != expected or platforms != ['linux/arm64']:
    raise SystemExit('Published digest or architecture differs from the verified candidate; do not deploy')
PY
echo "Published immutable reference: $registry_repo@$candidate_digest"
