#!/usr/bin/env python3
"""Verify a running local Compose stack. --recreate briefly recreates its services.
Creates synthetic jobs and a temporary MinIO bucket; cleans up the bucket only.
"""
import argparse
import datetime
import hashlib
import hmac
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
import urllib.request
import uuid

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--recreate', action='store_true', help='Recreate project containers and verify named-volume persistence')
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
env = dict(os.environ)
desktop = Path('/Applications/Docker.app/Contents/Resources/bin')
if desktop.exists(): env['PATH'] = str(desktop) + os.pathsep + env.get('PATH', '')
docker = shutil.which('docker', path=env['PATH'])
if not docker: raise SystemExit('Docker CLI not found')

def command(*parts, expected=0):
    result = subprocess.run([docker, *parts], cwd=root, env=env, capture_output=True, text=True, timeout=120)
    if result.returncode != expected:
        # Avoid echoing resolved Compose configuration, which can contain credentials.
        raise AssertionError(f'Docker command failed (exit {result.returncode}): {parts[:3]}\n{result.stderr[-2000:]}')
    return result.stdout

def compose(*parts, **kwargs): return command('compose', *parts, **kwargs)

def request(path, body=None):
    payload = None if body is None else json.dumps(body).encode()
    headers = {'Content-Type': 'application/json'}
    key = config['services']['api']['environment'].get('Quality__Api__Key')
    if key: headers['Authorization'] = 'Bearer ' + key
    req = urllib.request.Request('http://127.0.0.1:5080' + path, data=payload, headers=headers)
    with urllib.request.urlopen(req, timeout=5) as response:
        return response.status, json.load(response)

def wait_ready():
    for _ in range(100):
        try:
            if request('/ready')[0] == 200: return
        except (OSError, ValueError): pass
        time.sleep(.2)
    raise AssertionError('API did not become ready')

def wait_completed(job_id):
    for _ in range(100):
        job = request('/jobs/' + job_id)[1]
        if job['status'] == 'Completed': return job
        if job['status'] == 'Failed': raise AssertionError('Planning failed')
        time.sleep(.2)
    raise AssertionError('Worker did not finish job')

config = json.loads(compose('--profile', '*', 'config', '--format', 'json'))
minio_env = config['services']['minio']['environment']
access = minio_env['MINIO_ROOT_USER']
secret = minio_env['MINIO_ROOT_PASSWORD']

def s3(method, path, data=b''):
    # AWS SigV4 for the local S3-compatible dependency; no third-party client required.
    now = datetime.datetime.now(datetime.timezone.utc)
    stamp, day = now.strftime('%Y%m%dT%H%M%SZ'), now.strftime('%Y%m%d')
    digest = hashlib.sha256(data).hexdigest()
    host = '127.0.0.1:9000'
    headers = f'host:{host}\nx-amz-content-sha256:{digest}\nx-amz-date:{stamp}\n'
    signed = 'host;x-amz-content-sha256;x-amz-date'
    canonical = f'{method}\n{path}\n\n{headers}\n{signed}\n{digest}'
    scope = f'{day}/us-east-1/s3/aws4_request'
    to_sign = f'AWS4-HMAC-SHA256\n{stamp}\n{scope}\n{hashlib.sha256(canonical.encode()).hexdigest()}'
    def mac(key, value): return hmac.new(key, value.encode(), hashlib.sha256).digest()
    key = mac(mac(mac(mac(('AWS4'+secret).encode(), day), 'us-east-1'), 's3'), 'aws4_request')
    signature = hmac.new(key, to_sign.encode(), hashlib.sha256).hexdigest()
    req = urllib.request.Request('http://' + host + path, method=method,
        data=data if method == 'PUT' else None,
        headers={'x-amz-date': stamp, 'x-amz-content-sha256': digest,
                 'Authorization': f'AWS4-HMAC-SHA256 Credential={access}/{scope}, SignedHeaders={signed}, Signature={signature}'})
    with urllib.request.urlopen(req, timeout=10) as response: return response.read()

wait_ready()
status, submitted = request('/jobs', {'reference': {'source': 'jira', 'id': 'DOCKER-1'}})
assert status == 202
job = wait_completed(submitted['id'])
assert [t['status'] for t in job['transitions']] == ['Queued', 'Normalizing', 'Planning', 'Completed']
assert job['testPlan']['isStub']
oneshot = json.loads(compose('run', '--rm', '--no-deps', 'oneshot', 'run', '--source', 'jira', '--reference', 'DOCKER-2'))
assert oneshot['status'] == 'Completed'
assert request('/jobs/'+oneshot['id'])[1]['testPlan']['id'] == oneshot['testPlan']['id']
assert json.loads(compose('run', '--rm', '--no-deps', 'oneshot', 'get', '--id', job['id']))['status'] == 'Completed'
compose('run', '--rm', '--no-deps', 'oneshot', 'run', '--source', 'jira', '--reference', 'invalid', expected=2)
compose('run', '--rm', '--no-deps', 'oneshot', 'get', '--id', '00000000000000000000000000000000', expected=3)
image_ids = set()
for service in ['api', 'worker']:
    container = json.loads(command('inspect', compose('ps', '-q', service).strip()))[0]
    image_ids.add(container['Image'])
    assert container['HostConfig']['ReadonlyRootfs']
    assert container['Config']['User'] == 'pwuser'
    assert container['State']['Running']
assert len(image_ids) == 1
assert config['services']['api']['image'] == config['services']['oneshot']['image'] == config['services']['smoke']['image']
runtimes = compose('exec', '-T', 'api', 'dotnet', '--list-runtimes')
assert 'Microsoft.AspNetCore.App 10.0.12' in runtimes
bucket = '/quality-smoke-' + uuid.uuid4().hex
created = False
try:
    s3('PUT', bucket); created = True
    s3('PUT', bucket + '/probe.txt', b'quality-system persistence probe')
    assert s3('GET', bucket + '/probe.txt') == b'quality-system persistence probe'
    if args.recreate:
        compose('up', '--no-build', '--force-recreate', '-d', '--wait')
        wait_ready()
        assert request('/jobs/'+job['id'])[1]['testPlan']['id'] == job['testPlan']['id']
        assert s3('GET', bucket + '/probe.txt') == b'quality-system persistence probe'
        print('PASS: PostgreSQL job and MinIO object survived container recreation')
finally:
    if created:
        s3('DELETE', bucket + '/probe.txt')
        s3('DELETE', bucket)
print('PASS: Compose API/worker/one-shot, CLI exit codes, .NET 10 runtime, non-root/read-only image, MinIO S3 round-trip')
print('Application image ID: ' + next(iter(image_ids)))
