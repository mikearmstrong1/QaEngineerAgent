#!/usr/bin/env python3
"""Exercise API authentication over HTTP with temporary keys and storage."""
import http.client
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
with tempfile.TemporaryDirectory(prefix='quality-auth-') as directory:
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        port = sock.getsockname()[1]
    base = f'http://127.0.0.1:{port}'
    env = dict(os.environ, Quality__Store='File', Quality__DataDirectory=directory,
               Quality__Api__Key='', Quality__Api__AllowAnonymous='false',
               Quality__Requirements__Mode='Stub', Quality__Planning__Mode='Stub',
               Quality__Artifacts__Mode='Local', Quality__RunWorker='false', ASPNETCORE_URLS=base)
    for key in ['', 'too-short', 'x' * 257, 'x' * 32 + ' ']:
        result = subprocess.run(['dotnet', str(dll), 'api'], cwd=root,
                                env=dict(env, Quality__Api__Key=key), capture_output=True, timeout=15)
        assert result.returncode == 2, 'Invalid configuration must fail startup'
    key = secrets.token_hex(32)
    env.update(Quality__Api__Key=key, Quality__Api__AllowAnonymous='true')
    # A configured key takes precedence over the demo setting.
    with open(Path(directory) / 'server.log', 'w') as log:
        process = subprocess.Popen(['dotnet', str(dll), 'api'], cwd=root, env=env, stdout=log, stderr=log)
        def request(path, token=None, data=None, idempotency_key=None):
            headers = {'Content-Type': 'application/json'}
            if idempotency_key is not None:
                headers['Idempotency-Key'] = idempotency_key
            if token is not None:
                headers['Authorization'] = token
            req = urllib.request.Request(base + path, headers=headers, data=data)
            try:
                response = urllib.request.urlopen(req, timeout=2)
            except urllib.error.HTTPError as error:
                response = error
            with response:
                return response.status, response.headers, response.read()
        try:
            for _ in range(100):
                try:
                    if request('/ready')[0] == 200:
                        break
                except OSError:
                    pass
                time.sleep(.1)
            else:
                raise AssertionError('API failed to start')
            assert request('/health')[0] == 200
            assert request('/')[0] == 200
            for token in [None, 'Basic ' + key, 'Bearer wrong', 'Bearer ' + secrets.token_hex(32), 'Bearer ' + key + ', Bearer ' + key]:
                for path, body in [('/jobs', b'{invalid'), ('/jobs/not-an-id', None), ('/jobs/not-an-id/cancel', b'')]:
                    status, headers, content = request(path, token, body)
                    assert status == 401
                    assert headers['WWW-Authenticate'] == 'Bearer'
                    assert key.encode() not in content
            connection = http.client.HTTPConnection('127.0.0.1', port, timeout=2)
            connection.putrequest('GET', '/jobs/not-an-id')
            connection.putheader('Authorization', 'Bearer ' + key)
            connection.putheader('Authorization', 'Bearer ' + key)
            connection.endheaders()
            response = connection.getresponse()
            assert response.status == 401
            response.read()
            connection.close()
            assert not list(Path(directory).glob('*.json')), 'Rejected submission created a job'
            payload = json.dumps({'reference': {'source': 'stub', 'id': 'auth-check'}}).encode()
            status, _, body = request('/jobs', 'Bearer ' + key, payload, idempotency_key='cancel-replay')
            assert status == 202
            job = json.loads(body)
            assert request('/jobs/' + job['id'], 'bearer ' + key)[0] == 200
            assert request('/jobs/' + job['id'])[0] == 401
            assert request('/jobs?api_key=' + key, data=payload)[0] == 401
            assert request('/jobs', 'Bearer ' + key, b'{invalid')[0] == 400
            cancel_url = '/jobs/' + job['id'] + '/cancel'
            assert request(cancel_url, data=b'')[0] == 401
            assert json.loads(request('/jobs/' + job['id'], 'Bearer ' + key)[2])['status'] == 'Queued'
            assert request('/jobs/bad/cancel', 'Bearer ' + key, b'')[0] == 400
            assert request('/jobs/' + '0' * 32 + '/cancel', 'Bearer ' + key, b'')[0] == 404
            status, _, body = request(cancel_url, 'Bearer ' + key, b'')
            cancelled = json.loads(body)
            assert status == 200 and cancelled['status'] == 'Cancelled'
            assert cancelled['leaseToken'] is None and cancelled['isTerminal']
            validate = subprocess.run(['node', '-e', '''
const fs = require('node:fs');
const Ajv = require('ajv/dist/2020');
const ajv = new Ajv({allErrors:true, strict:true});
require('ajv-formats')(ajv);
for (const name of fs.readdirSync('schemas/v1').filter(n => n.endsWith('.schema.json')))
  ajv.addSchema(JSON.parse(fs.readFileSync('schemas/v1/' + name, 'utf8')));
const validate = ajv.getSchema('https://quality.local/schemas/v1/job.schema.json');
if (!validate(JSON.parse(fs.readFileSync(0, 'utf8')))) throw new Error(JSON.stringify(validate.errors));
'''], input=body, cwd=root, capture_output=True, timeout=10)
            assert validate.returncode == 0, validate.stderr

            assert json.loads(request(cancel_url, 'Bearer ' + key, b'')[2]) == cancelled

            def cli(*args):
                return subprocess.run(['dotnet', str(dll), *args], cwd=root, env=env,
                                      text=True, capture_output=True, timeout=30)

            repeat = cli('cancel', '--id', job['id'])
            assert repeat.returncode == 0 and json.loads(repeat.stdout) == cancelled
            assert cli('get', '--id', job['id']).returncode == 1
            replay = cli('run', '--source', 'stub', '--reference', 'auth-check', '--idempotency-key', 'cancel-replay')
            assert replay.returncode == 1 and json.loads(replay.stdout) == cancelled
            assert cli('cancel', '--id', '0' * 32).returncode == 3
            assert cli('cancel', '--id', 'bad').returncode == 2
            completed = cli('run', '--source', 'stub', '--reference', 'terminal-cancel')
            assert completed.returncode == 0, completed.stderr
            completed_job = json.loads(completed.stdout)
            terminal_url = '/jobs/' + completed_job['id'] + '/cancel'
            status, _, body = request(terminal_url, 'Bearer ' + key, b'')
            assert status == 409 and json.loads(body)['error'] == 'job_already_terminal'
            assert cli('cancel', '--id', completed_job['id']).returncode == 4
            assert json.loads(request('/jobs/' + completed_job['id'], 'Bearer ' + key)[2]) == completed_job
            # A fresh process processes another job without reviving the canceled one.
            assert json.loads(request('/jobs/' + job['id'], 'Bearer ' + key)[2]) == cancelled
            # Also exercise first-time CLI cancellation on a queued API submission.
            queued = json.loads(request('/jobs', 'Bearer ' + key, payload)[2])
            result = cli('cancel', '--id', queued['id'])
            assert result.returncode == 0 and json.loads(result.stdout)['status'] == 'Cancelled'

        finally:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
    print('PASS: fail-closed configuration, public probes, bearer validation, protected reads/writes, durable HTTP/CLI cancellation, terminal conflict, no rejected-job side effects')
