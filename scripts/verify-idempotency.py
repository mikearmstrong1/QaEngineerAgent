"""Check CLI replay in isolated local storage, without external providers."""
import json
import os
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory(prefix="quality-idempotency-cli-") as directory:
    env = dict(os.environ, Quality__Store="File", Quality__DataDirectory=directory,
               Quality__Requirements__Mode="Stub", Quality__Planning__Mode="Stub")
    command = ["dotnet", str(root / "src/Quality.Api/bin/Release/net10.0/Quality.Api.dll"),
               "run", "--source", "stub", "--reference", "cli-idempotency",
               "--idempotency-key", "cli-retry-key"]

    def run(args):
        return subprocess.run(args, cwd=root, env=env, text=True, capture_output=True, timeout=30)

    first_result, second_result = run(command), run(command)
    assert first_result.returncode == second_result.returncode == 0, (first_result.stderr, second_result.stderr)
    first, second = json.loads(first_result.stdout), json.loads(second_result.stdout)
    assert first["id"] == second["id"] and first["revision"] == second["revision"]
    assert second["status"] == "Completed"
    conflict = command.copy()
    conflict[6] = "different"
    assert run(conflict).returncode != 0
    assert len(list(Path(directory).glob("*.json"))) == 1
    print("CLI retry and conflicting-key checks passed; one persisted job.")
