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
        def request(path, token=None, data=None):
            headers = {'Content-Type': 'application/json'}
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
                for path, body in [('/jobs', b'{invalid'), ('/jobs/not-an-id', None)]:
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
            status, _, body = request('/jobs', 'Bearer ' + key, payload)
            assert status == 202
            job = json.loads(body)
            assert request('/jobs/' + job['id'], 'bearer ' + key)[0] == 200
            assert request('/jobs/' + job['id'])[0] == 401
            assert request('/jobs?api_key=' + key, data=payload)[0] == 401
            assert request('/jobs', 'Bearer ' + key, b'{invalid')[0] == 400
        finally:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
    print('PASS: fail-closed configuration, public probes, bearer validation, protected reads/writes, no rejected-job side effects')
