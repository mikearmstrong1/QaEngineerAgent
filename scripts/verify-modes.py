#!/usr/bin/env python3
"""Verify distinct API/worker processes, restart persistence, and CLI contracts.
Build Release first. Uses a temporary file store, or QUALITY_TEST_POSTGRES if set.
"""
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
import urllib.request

root = Path(__file__).resolve().parents[1]
dll = root / "src/Quality.Api/bin/Release/net10.0/Quality.Api.dll"

def run(env, *args, expected=0):
    result = subprocess.run(["dotnet", str(dll), *args], cwd=root, env=env, text=True, capture_output=True, timeout=30)
    assert result.returncode == expected, (args, result.returncode, result.stderr)
    return result.stdout

def stop(process):
    process.terminate()
    try: process.wait(timeout=10)
    except subprocess.TimeoutExpired: process.kill(); process.wait(timeout=5)

with tempfile.TemporaryDirectory(prefix="quality-modes-") as directory:
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    env = dict(os.environ, Quality__Store="File", Quality__DataDirectory=directory,
               Quality__RunWorker="false", ASPNETCORE_URLS=f"http://127.0.0.1:{port}")
    if os.environ.get("QUALITY_TEST_POSTGRES"):
        env.update(Quality__Store="Postgres", ConnectionStrings__Quality=os.environ["QUALITY_TEST_POSTGRES"])
    processes = []
    with open(Path(directory) / "process.log", "w") as log:
        def start(mode):
            process = subprocess.Popen(["dotnet", str(dll), mode], cwd=root, env=env, stdout=log, stderr=log)
            processes.append(process)
            return process
        def get(path):
            with urllib.request.urlopen(f"http://127.0.0.1:{port}{path}", timeout=2) as response:
                return json.load(response)
        def ready():
            for _ in range(100):
                try:
                    if get('/ready')['status'] == 'ready': return
                except (OSError, ValueError): pass
                time.sleep(.1)
            raise AssertionError('API never became ready')
        try:
            api = start('api'); ready()
            request = urllib.request.Request(f"http://127.0.0.1:{port}/jobs",
                data=json.dumps({'reference': {'source': 'jira', 'id': 'AUTH-1427'}}).encode(),
                headers={'Content-Type': 'application/json'})
            with urllib.request.urlopen(request, timeout=5) as response:
                assert response.status == 202
                job = json.load(response)
                assert response.headers['Location'] == f"/jobs/{job['id']}"
            assert get(f"/jobs/{job['id']}")['status'] == 'Queued'
            worker = start('worker')
            for _ in range(100):
                completed = get(f"/jobs/{job['id']}")
                if completed['status'] == 'Completed': break
                time.sleep(.1)
            assert completed['status'] == 'Completed', completed
            assert worker.poll() is None, 'Worker failed; it must not compete with API for its port'
            stop(worker); stop(api)
            api = start('api'); ready()
            assert get(f"/jobs/{job['id']}")['testPlan']['id'] == completed['testPlan']['id']
            read = json.loads(run(env, 'get', '--id', job['id']))
            assert read['status'] == 'Completed'
            one_shot = json.loads(run(env, 'run', '--source', 'jira', '--reference', 'CLI-1'))
            assert one_shot['status'] == 'Completed'
            assert get(f"/jobs/{one_shot['id']}")['status'] == 'Completed'
            run(env, 'run', '--source', 'jira', '--reference', 'invalid', expected=2)
            run(env, 'run', '--source', 'jira', expected=2)
            run(env, 'unknown', expected=2)
            run(env, 'get', '--id', '00000000000000000000000000000000', expected=3)
            print(f"PASS: {env['Quality__Store']} API/worker separation, restart persistence, CLI JSON and exit codes")
        finally:
            for process in reversed(processes):
                if process.poll() is None: stop(process)
