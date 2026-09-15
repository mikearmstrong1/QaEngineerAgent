// Opt-in regression suite. Never picked up by the service's default smoke suite.
const path = require('node:path');
module.exports = {
  testDir: path.join(__dirname, 'regressions'), testMatch: '**/test.spec.cjs',
  workers: 1, retries: 0, forbidOnly: true, timeout: 30000, globalTimeout: 300000,
  outputDir: path.join(__dirname, '../data/regression-results'),
  reporter: [['list'], ['json', { outputFile: path.join(__dirname, '../data/regression-report.json') }]],
  use: { browserName: 'chromium', headless: true, serviceWorkers: 'block', acceptDownloads: false,
    trace: 'retain-on-failure', screenshot: 'only-on-failure', navigationTimeout: 10000, actionTimeout: 5000 }
};
