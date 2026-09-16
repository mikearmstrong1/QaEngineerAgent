#!/usr/bin/env python3
"""Verify authenticated, process-local metrics from separate API and worker hosts."""
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

root = Path(__file__).resolve().parents[1]
dll = root / 'src/Quality.Api/bin/Release/net10.0/Quality.Api.dll'


def free_url():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return f'http://127.0.0.1:{sock.getsockname()[1]}'


def stop(process):
    process.terminate()
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)


with tempfile.TemporaryDirectory(prefix='quality-metrics-') as directory:
    api_url, worker_url = free_url(), free_url()
    while worker_url == api_url:
        worker_url = free_url()
    key = secrets.token_hex(32)
    env = dict(os.environ, Quality__Store='File', Quality__DataDirectory=directory,
               Quality__Api__Key=key, Quality__Api__AllowAnonymous='false',
               Quality__Requirements__Mode='Stub', Quality__Planning__Mode='Stub',
               Quality__Artifacts__Mode='Local', Quality__RunWorker='false',
               ASPNETCORE_URLS=api_url, Quality__Metrics__WorkerUrl=worker_url)
    processes = []

    def request(base, endpoint, authenticated=True, body=None, extra=None):
        headers = {'Content-Type': 'application/json', **(extra or {})}
        if authenticated:
            headers['Authorization'] = 'Bearer ' + key
        req = urllib.request.Request(base + endpoint, headers=headers, data=body)
        try:
            response = urllib.request.urlopen(req, timeout=3)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            return response.status, response.headers, response.read().decode()

    def metrics(base):
        status, headers, text = request(base, '/metrics')
        assert status == 200
        assert headers['Content-Type'].startswith('text/plain; version=0.0.4')
        assert headers['Cache-Control'] == 'no-store'
        assert key not in text and 'private-reference' not in text
        # Reject duplicate samples; check count/+Inf consistency independently of the collector.
        samples = {}
        for line in text.splitlines():
            if not line or line.startswith('#'):
                continue
            name, value = line.rsplit(' ', 1)
            assert name not in samples, name
            samples[name] = float(value)
        for name, value in samples.items():
            if '_bucket{' in name and ',le="+Inf"' in name:
                count = name.replace('_bucket{', '_count{').replace(',le="+Inf"', '')
                assert samples[count] == value
        return samples

    def sample(values, operation, outcome):
        return values[f'quality_operations_total{{operation="{operation}",outcome="{outcome}"}}']

    with open(Path(directory) / 'process.log', 'w') as log:
        def start(mode):
            process = subprocess.Popen(['dotnet', str(dll), mode], cwd=root, env=env, stdout=log, stderr=log)
            processes.append(process)
            base = api_url if mode == 'api' else worker_url
            for _ in range(100):
                try:
                    if request(base, '/metrics')[0] == 200:
                        return process
                except OSError:
                    pass
                assert process.poll() is None, Path(directory, 'process.log').read_text()
                time.sleep(.1)
            raise AssertionError(f'{mode} never became ready')

        try:
            # Listener startup uses the API's fail-closed access policy.
            for override in [{'Quality__Api__Key': ''}, {'Quality__Metrics__WorkerUrl': 'not-a-url'}]:
                result = subprocess.run(['dotnet', str(dll), 'worker'], cwd=root, env=dict(env, **override),
                                        capture_output=True, timeout=15)
                assert result.returncode == 2
            api = start('api')
            for base in [api_url]:
                assert request(base, '/metrics', authenticated=False)[0] == 401
            before = metrics(api_url)
            assert metrics(api_url) == before, 'Scrapes changed the counters'
            payload = json.dumps({'reference': {'source': 'stub', 'id': 'private-reference'}}).encode()
            headers = {'Idempotency-Key': 'private-submission-key'}
            status, _, body = request(api_url, '/jobs', body=payload, extra=headers)
            assert status == 202
            job = json.loads(body)
            request(api_url, '/jobs', body=payload, extra=headers)
            conflict = json.dumps({'reference': {'source': 'stub', 'id': 'different'}}).encode()
            assert request(api_url, '/jobs', body=conflict, extra=headers)[0] == 409
            values = metrics(api_url)
            assert sample(values, 'submit', 'success') == 1
            assert sample(values, 'submit', 'replayed') == 1
            assert sample(values, 'submit', 'conflict') == 1
            assert sample(values, 'attempt', 'completed') == 0
            worker = start('worker')
            assert request(worker_url, '/metrics', authenticated=False)[0] == 401
            assert request(worker_url, '/jobs')[0] == 404, 'Worker must expose only metrics'
            for _ in range(100):
                if sample(metrics(worker_url), 'attempt', 'completed') == 1:
                    break
                time.sleep(.1)
            else:
                raise AssertionError('Worker never recorded completion')
            values = metrics(worker_url)
            assert sample(values, 'normalize', 'success') == 1
            assert sample(values, 'plan', 'success') == 1
            assert values['quality_operations_active{operation="attempt"}'] == 0
            assert sample(values, 'submit', 'success') == 0
            assert sample(metrics(api_url), 'attempt', 'completed') == 0
            assert job['id'] not in request(worker_url, '/metrics')[2]
            stop(worker)
            worker = start('worker')
            assert sample(metrics(worker_url), 'attempt', 'completed') == 0, 'Restart must reset process metrics'
            assert json.loads(request(api_url, '/jobs/' + job['id'])[2])['status'] == 'Completed'
            stop(worker)
            queued = json.loads(request(api_url, '/jobs', body=payload)[2])
            endpoint = '/jobs/' + queued['id'] + '/cancel'
            assert request(api_url, endpoint, body=b'')[0] == 200
            assert request(api_url, endpoint, body=b'')[0] == 200
            assert sample(metrics(api_url), 'cancel', 'cancelled') == 2
            print('PASS: authenticated API/worker metrics, bounded labels, histogram consistency, replay/conflict/cancel counts, process isolation and restart reset')
        finally:
            for process in reversed(processes):
                if process.poll() is None:
                    stop(process)
