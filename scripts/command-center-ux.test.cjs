const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { test } = require('node:test');

const app = readFileSync('src/Quality.CommandCenter/wwwroot/app.js', 'utf8');
const styles = readFileSync('src/Quality.CommandCenter/wwwroot/execution.css', 'utf8');

test('recent jobs expose stable identity and planning-provider context', () => {
  assert.match(app, /job\.id\.slice\(0,8\)/);
  assert.match(app, /planning\.provider/);
  assert.match(app, /planning\.model\|\|planning\.requestedModel/);
  assert.match(app, /setAttribute\('aria-label'/);
  assert.match(styles, /\.job-context/);
});

test('policy administration is collapsed and identified as advanced', () => {
  assert.match(app, /node\('details',undefined,'policy-administration'\)/);
  assert.match(app, /Advanced: execution policy administration/);
  assert.match(app, /Routine planning and reviewed execution do not require changes here/);
  assert.match(styles, /\.policy-administration > summary/);
});

test('an existing immutable version cannot be presented as an in-place save', () => {
  assert.match(app, /Save as new revision/);
  assert.match(app, /Version \$\{existingVersion\} already exists and is immutable/);
  assert.match(app, /Create policy revision/);
  assert.doesNotMatch(app, /node\('button','Save policy'\)/);
});
