// Trusted runner: no installation, shell invocation or model-generated JavaScript.
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const directory = process.argv[2];
const version = require('@playwright/test/package.json').version;
const writeResult = result => fs.writeFileSync(path.join(directory, 'result.json'), JSON.stringify({ ...result, executorVersion: `playwright/${version};quality-executor/v2` }));
try {
  const manifest = JSON.parse(fs.readFileSync(path.join(directory, 'manifest.json'), 'utf8'));
  const origins = JSON.parse(fs.readFileSync(path.join(directory, 'origins.json'), 'utf8'));
  const packagePath = require.resolve('@playwright/test');
  const spec = `const { test, expect } = require(${JSON.stringify(packagePath)});
const { register } = require(${JSON.stringify(require.resolve('./register.cjs'))});
register(require('./manifest.json'), require('./origins.json'), test, expect);
`;
  fs.writeFileSync(path.join(directory, 'generated.spec.cjs'), spec);
  fs.copyFileSync(path.join(__dirname, 'reporter.cjs'), path.join(directory, 'reporter.cjs'));
  const config = {
    testDir: directory, testMatch: 'generated.spec.cjs', fullyParallel: false, workers: 1, retries: 0,
    forbidOnly: true, timeout: Math.min(30000, manifest.timeoutSeconds * 1000), globalTimeout: manifest.timeoutSeconds * 1000,
    expect: { timeout: Math.min(5000, manifest.timeoutSeconds * 1000) },
    outputDir: path.join(directory, 'artifacts'),
    reporter: [[path.join(directory, 'reporter.cjs')], ['json', { outputFile: path.join(directory, 'report.json') }]],
    use: { browserName: 'chromium', headless: true, serviceWorkers: 'block', acceptDownloads: false,
      trace: 'retain-on-failure', screenshot: 'only-on-failure', navigationTimeout: 10000, actionTimeout: 5000 }
  };
  fs.writeFileSync(path.join(directory, 'playwright.config.cjs'), 'module.exports = ' + JSON.stringify(config));
  const log = fs.openSync(path.join(directory, 'process.log'), 'w');
  let child;
  try {
    child = spawnSync(process.execPath, [require.resolve('@playwright/test/cli'), 'test', '--config', path.join(directory, 'playwright.config.cjs')],
      { cwd: directory, env: process.env, stdio: ['ignore', log, log] });
  } finally { fs.closeSync(log); }
  const summary = JSON.parse(fs.readFileSync(path.join(directory, 'summary.json'), 'utf8'));
  const actual = summary.tests.map(test => test.id).sort();
  const expected = manifest.tests.map(test => test.testCaseId).sort();
  let status = 'InfrastructureFailed';
  if (summary.status === 'timedout') status = 'TimedOut';
  else if (!summary.errors && JSON.stringify(actual) === JSON.stringify(expected) && summary.tests.length > 0) {
    if (summary.status === 'passed' && child.status === 0 && summary.tests.every(test => test.status === 'passed')) status = 'Passed';
    else if (child.status !== 0 && summary.tests.some(test => ['failed', 'timedOut'].includes(test.status))) status = 'Failed';
  }
  const kinds = new Set(summary.tests.filter(test => test.status !== 'passed').map(test => test.classification));
  const classification = status === 'Passed' ? 'None' : ['InfrastructureFailed', 'TimedOut'].includes(status) ? 'InfrastructureFailure'
    : kinds.size === 1 ? [...kinds][0] : 'NeedsReview';
  writeResult({ status, testCaseIds: actual, failureClassification: classification ?? 'NeedsReview' });
  process.exitCode = status === 'Passed' ? 0 : 1;
} catch {
  writeResult({ status: 'InfrastructureFailed', testCaseIds: [], failureClassification: 'InfrastructureFailure' });
  process.exitCode = 1;
}
