#!/usr/bin/env python3
"""Container controls check. Requires the verification API running with its worker stopped.
Uses the selected COMPOSE_FILE/COMPOSE_PROJECT_NAME and QUALITY_VERIFY_API_URL.
"""
import json
import os
import subprocess
import urllib.request
import urllib.error
API = os.environ.get("QUALITY_VERIFY_API_URL", "http://127.0.0.1:5080")
config = json.loads(subprocess.check_output(["docker", "compose", "config", "--format", "json"]))
key = config["services"]["api"]["environment"].get("Quality__Api__Key")
if not key:
    raise SystemExit("Container controls verification requires bearer authentication")

def request(base, endpoint, token=True, body=None, headers=None):
    combined = {"Content-Type": "application/json", **(headers or {})}
    if token: combined["Authorization"] = "Bearer " + key
    req = urllib.request.Request(base + endpoint, headers=combined,
        data=None if body is None else json.dumps(body).encode())
    try: response = urllib.request.urlopen(req, timeout=5)
    except urllib.error.HTTPError as error: response = error
    with response:
        text = response.read().decode()
        return response.status, json.loads(text) if "application/json" in response.headers.get("Content-Type", "") else text

def verify_controls(request):
    import uuid
    # 1. GET /metrics without token -> 401
    status, _ = request(API, "/metrics", token=False)
    assert status == 401, f"Expected 401 for unauthenticated metrics, got {status}"

    # 2. GET /metrics with token -> 200, text contains specific metric
    status, body = request(API, "/metrics", token=True)
    assert status == 200, f"Expected 200 for authenticated metrics, got {status}"
    assert isinstance(body, str) and "# TYPE quality_operations_total counter" in body, \
        f"Expected metric text, got: {repr(body)[:100]}"

    # 3. POST /jobs with idempotency key and specific body -> 202 queued
    job_id = "cancel-probe"
    idem_key = str(uuid.uuid4())
    body = {"reference": {"source": "stub", "id": job_id}}
    headers = {"Idempotency-Key": idem_key}
    status, resp = request(API, "/jobs", token=True, body=body, headers=headers)
    assert status == 202, f"Expected 202 for job submission, got {status}"
    assert resp.get("status") == "Queued", f"Expected queued status, got {resp.get('status')}"

    job_id = resp["id"]
    # 4. Cancel the job -> 200 Cancelled
    status, resp = request(API, f"/jobs/{job_id}/cancel", token=True, body={})
    assert status == 200, f"Expected 200 for cancel, got {status}"
    assert resp.get("status") == "Cancelled", f"Expected Cancelled, got {resp.get('status')}"

    cancelled = resp
    # 5. Repeat cancel same snapshot -> should still be 200 Cancelled (idempotent)
    status, resp = request(API, f"/jobs/{job_id}/cancel", token=True, body={})
    assert status == 200, f"Expected 200 for repeat cancel, got {status}"
    assert resp.get("status") == "Cancelled", f"Expected Cancelled, got {resp.get('status')}"

    assert resp == cancelled
    # 6. Repeat submission with same idempotency key and same id -> 202 but status Cancelled
    status, resp = request(API, "/jobs", token=True, body=body, headers=headers)
    assert status == 202, f"Expected 202 for repeat submission, got {status}"
    assert resp.get("status") == "Cancelled", f"Expected Cancelled for repeat submission, got {resp.get('status')}"

    assert resp == cancelled
    # 7. Invalid cancel (non-existent job) -> 400
    status, _ = request(API, "/jobs/invalid-id/cancel", token=True, body={})
    assert status == 400, f"Expected 400 for invalid cancel, got {status}"

    # 8. Missing job (32 zeros) cancel -> 404
    missing_id = "0" * 32
    status, _ = request(API, f"/jobs/{missing_id}/cancel", token=True, body={})
    assert status == 404, f"Expected 404 for missing job cancel, got {status}"

    # 9. No auth cancel -> 401
    status, _ = request(API, f"/jobs/{job_id}/cancel", token=False, body={})
    assert status == 401, f"Expected 401 for unauthenticated cancel, got {status}"

    return job_id

if __name__ == "__main__":
    cancelled = verify_controls(request)
    print("PASS: container metrics auth, queued cancellation, repeat cancellation and idempotent replay; cancelled job " + cancelled)
