import { defineConfig } from '@playwright/test';
import path from 'node:path';
import os from 'node:os';
const artifacts = process.env.QUALITY_ARTIFACT_DIR;
const baseURL = process.env.QUALITY_BASE_URL ?? `http://127.0.0.1:${process.env.QUALITY_SMOKE_PORT ?? '5080'}`;
const runRoot = path.join(os.tmpdir(), 'quality-smoke', String(process.pid));
const dataDirectory = path.join(runRoot, 'jobs');
const executionDirectory = path.join(runRoot, 'executions');
export default defineConfig({
  testDir: './tests', fullyParallel: true, retries: 0,
  outputDir: artifacts ? path.join(artifacts, 'test-results') : './test-results',
  reporter: [['list'], ['html', { open: 'never', outputFolder: artifacts ? path.join(artifacts, 'report') : './playwright-report' }]],
  use: { baseURL, trace: 'off', extraHTTPHeaders: process.env.QUALITY_API_KEY ? { Authorization: `Bearer ${process.env.QUALITY_API_KEY}` } : {} },
  projects: [{ name: 'chromium', use: { browserName: 'chromium' } }],
  webServer: process.env.QUALITY_BASE_URL ? undefined : {
    command: 'dotnet ../src/Quality.Api/bin/Release/net10.0/Quality.Api.dll api',
    url: `${baseURL}/ready`, reuseExistingServer: false, timeout: 30000,
    env: { Quality__Api__Key: process.env.QUALITY_API_KEY ?? '', Quality__Api__AllowAnonymous: process.env.QUALITY_API_KEY ? 'false' : 'true', ASPNETCORE_URLS: baseURL, Quality__Store: 'File', Quality__RunWorker: 'true', Logging__LogLevel__Default: 'Warning',
      Quality__Artifacts__Mode: 'Local', Quality__Requirements__Mode: 'Stub', Quality__Planning__Mode: 'Stub',
      Quality__DataDirectory: dataDirectory,
      Quality__Execution__RunDirectory: executionDirectory },
  },
});
