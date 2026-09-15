import { defineConfig } from '@playwright/test';
import path from 'node:path';
const artifacts = process.env.QUALITY_ARTIFACT_DIR;
const baseURL = process.env.QUALITY_BASE_URL ?? `http://127.0.0.1:${process.env.QUALITY_SMOKE_PORT ?? '5080'}`;
export default defineConfig({
  testDir: './tests', fullyParallel: true, retries: 0,
  outputDir: artifacts ? path.join(artifacts, 'test-results') : './test-results',
  reporter: [['list'], ['html', { open: 'never', outputFolder: artifacts ? path.join(artifacts, 'report') : './playwright-report' }]],
  use: { baseURL, trace: 'retain-on-failure' },
  projects: [{ name: 'chromium', use: { browserName: 'chromium' } }],
  webServer: process.env.QUALITY_BASE_URL ? undefined : {
    command: 'dotnet ../src/Quality.Api/bin/Release/net10.0/Quality.Api.dll api',
    url: `${baseURL}/ready`, reuseExistingServer: false, timeout: 30000,
    env: { ASPNETCORE_URLS: baseURL, Quality__Store: 'File', Quality__RunWorker: 'true', Logging__LogLevel__Default: 'Warning',
      Quality__Artifacts__Mode: 'Local', Quality__Requirements__Mode: 'Stub', Quality__Planning__Mode: 'Stub',
      Quality__DataDirectory: path.resolve(__dirname, '../data/smoke-jobs') },
  },
});
