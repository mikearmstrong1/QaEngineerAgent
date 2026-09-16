#!/usr/bin/env python3
"""Verify the MinIO adapter against this project's running Compose dependency.
Creates a unique temporary bucket, tests retention/uploads/reads/associations, and deletes it.
Requires a Release build of the .NET tests. Never prints resolved credentials.
"""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
env = dict(os.environ)
desktop = Path('/Applications/Docker.app/Contents/Resources/bin')
if desktop.exists():
    env['PATH'] = str(desktop) + os.pathsep + env.get('PATH', '')
config = subprocess.run(['docker', 'compose', 'config', '--format', 'json'], cwd=root, env=env,
                        capture_output=True, text=True, timeout=20, check=True)
credentials = json.loads(config.stdout)['services']['minio']['environment']
env.update(QUALITY_TEST_MINIO_ENDPOINT=env.get('QUALITY_VERIFY_S3_URL', 'http://127.0.0.1:9000'),
           QUALITY_TEST_MINIO_ACCESS_KEY=credentials['MINIO_ROOT_USER'],
           QUALITY_TEST_MINIO_SECRET_KEY=credentials['MINIO_ROOT_PASSWORD'])
with tempfile.TemporaryDirectory(prefix='quality-minio-check-') as reports:
    result = subprocess.run(['dotnet', 'test', 'QualitySystem.sln', '-c', 'Release', '--no-build', '-m:1',
                             '--filter', 'FullyQualifiedName~MinioIntegrationTests',
                             '--logger', 'trx;LogFileName=minio.trx', '--results-directory', reports],
                            cwd=root, env=env, timeout=120)
    if result.returncode:
        raise SystemExit(result.returncode)
    report = Path(reports) / 'minio.trx'
    if not report.exists():
        raise SystemExit('MinIO integration test report is missing')
    counters = ET.parse(report).find('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}Counters')
    if counters is None or counters.get('executed') != '1' or counters.get('passed') != '1':
        raise SystemExit('Expected one executed, passing MinIO integration test')
