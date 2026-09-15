const { test, before, after } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const http = require('node:http');
const crypto = require('node:crypto');
const { spawn } = require('node:child_process');
const root = path.resolve(__dirname, '..');
let directory, server, outside, origin, otherOrigin, base, foreignRequests = 0;
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
function cli(args, override = {}, cancelAfter) {
  return new Promise((resolve, reject) => {
    const child = spawn('dotnet', [path.join(root, 'src/Quality.Api/bin/Release/net10.0/Quality.Api.dll'), ...args], {
      cwd: root, env: { ...process.env, Quality__Store: 'File', Quality__DataDirectory: path.join(directory, 'jobs'),
        Quality__Artifacts__Mode: 'Local', Quality__Requirements__Mode: 'Stub', Quality__Planning__Mode: 'Stub', Quality__Execution__Workspace: root,
        Quality__Execution__RunDirectory: path.join(directory, 'runs'),
        Quality__SourceControl__Workspace: root, Quality__SourceControl__ProposalDirectory: path.join(directory, 'proposals'), Quality__Execution__AllowedOrigins: origin, ...override }
    });
    const timer = cancelAfter ? setTimeout(() => child.kill('SIGINT'), cancelAfter) : null;
    let stdout = '', stderr = '';
    child.stdout.on('data', data => stdout += data);
    child.stderr.on('data', data => stderr += data);
    child.on('error', reject);
    child.on('exit', code => { if (timer) clearTimeout(timer); resolve({ code, stdout, stderr }); });
  });
}
async function execute(manifest, overrides = {}, digest, cancelAfter) {
  const bytes = JSON.stringify(manifest);
  const file = path.join(directory, crypto.randomUUID() + '.json');
  fs.writeFileSync(file, bytes);
  return cli(['execute', '--job', base.job, '--manifest', file, '--sha256', digest ?? hash(bytes)], overrides, cancelAfter);
}
function manifest(steps) {
  return { ...base.manifest, timeoutSeconds: 15, tests: [{ testCaseId: 'TC-1', steps }] };
}
const step = (action, selector = null, value = null) => ({ action, selector, value });
before(async () => {
  directory = fs.mkdtempSync(path.join(os.tmpdir(), 'quality-execution-e2e-'));
  outside = http.createServer((req, res) => { foreignRequests++; res.end('outside'); });
  await new Promise(resolve => outside.listen(0, '127.0.0.1', resolve));
  otherOrigin = `http://127.0.0.1:${outside.address().port}`;
  server = http.createServer((req, res) => {
    if (req.url === '/redirect') { res.writeHead(302, { Location: otherOrigin }); res.end(); return; }
    res.setHeader('Content-Type', 'text/html');
    res.end('<!doctype html><html><h1>Ready</h1><label>Name<input id="name"></label><button onclick="document.querySelector(\'h1\').textContent=document.querySelector(\'input\').value">Show</button></html>');
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  origin = `http://127.0.0.1:${server.address().port}`;
  const job = await cli(['run', '--source', 'stub', '--reference', 'execution-fixture']);
  assert.equal(job.code, 0, job.stderr);
  const jobId = JSON.parse(job.stdout).id;
  const draft = await cli(['prepare-execution', '--job', jobId, '--target', origin]);
  assert.equal(draft.code, 0, draft.stderr);
  base = { job: jobId, manifest: JSON.parse(draft.stdout) };
});
after(async () => {
  await Promise.all([server, outside].filter(Boolean).map(server => new Promise(resolve => server.close(resolve))));
  fs.rmSync(directory, { recursive: true, force: true });
});
test('real Chromium execution passes and persists a schema-valid run', async () => {
  const result = await execute(manifest([step('goto', null, '/'), step('expectText', 'h1', 'Ready'), step('expectVisible', '#name'),
    step('fill', '#name', 'Reviewed'), step('click', 'button'), step('expectText', 'h1', 'Reviewed'), step('expectUrl', null, '/') ]));
  assert.equal(result.code, 0, result.stderr + result.stdout);
  const run = JSON.parse(result.stdout);
  assert.equal(run.status, 'Passed');
  assert.equal(run.failureClassification, 'None');
  assert.match(run.executorVersion, /^playwright\//);
  assert.ok(run.artifactKeys.some(key => key.endsWith('/report.json')));
  const read = await cli(['get-run', '--id', run.id]);
  assert.deepEqual(JSON.parse(read.stdout), run);
  const Ajv = require('ajv/dist/2020'); const ajv = new Ajv({ strict: true }); require('ajv-formats')(ajv);
  const validate = ajv.compile(JSON.parse(fs.readFileSync(path.join(root, 'schemas/v1/test-run.schema.json'))));
  assert.ok(validate(run), JSON.stringify(validate.errors));
});
test('a failing assertion is persisted as Failed with trace and screenshot', async () => {
  const result = await execute(manifest([step('goto', null, '/'), step('expectText', 'h1', 'Wrong')]));
  assert.equal(result.code, 1, result.stderr);
  const run = JSON.parse(result.stdout);
  assert.equal(run.status, 'Failed');
  assert.equal(run.failureClassification, 'NeedsReview');
  const classified = await cli(['classify-failure', '--run', run.id, '--classification', 'ApplicationFailure', '--reason', 'Fixture assertion differs from the required page text']);
  assert.equal(classified.code, 0, classified.stderr);
  assert.equal(JSON.parse(classified.stdout).status, 'Failed');
  assert.equal(JSON.parse(classified.stdout).failureClassification, 'ApplicationFailure');
  assert.ok(run.artifactKeys.some(key => key.endsWith('.png')));
  assert.ok(run.artifactKeys.some(key => key.endsWith('.zip')));
});
test('review checksum and origin policy reject execution before run creation', async () => {
  const m = manifest([step('goto', null, '/'), step('expectVisible', 'h1')]);
  const before = fs.readdirSync(path.join(directory, 'runs')).length;
  assert.equal((await execute(m, {}, '0'.repeat(64))).code, 2);
  assert.equal((await execute(m, { Quality__Execution__AllowedOrigins: otherOrigin })).code, 2);
  assert.equal(fs.readdirSync(path.join(directory, 'runs')).length, before);
});
test('redirects cannot send a request to an unallowlisted origin', async () => {
  const result = await execute(manifest([step('goto', null, '/redirect'), step('expectVisible', 'h1')]));
  assert.equal(JSON.parse(result.stdout).status, 'Failed');
  assert.equal(JSON.parse(result.stdout).failureClassification, 'InfrastructureFailure');
  assert.equal(foreignRequests, 0);
});
test('global timeout is a terminal nonpassing run', async () => {
  const m = manifest([step('goto', null, '/'), step('expectVisible', '#never')]);
  m.timeoutSeconds = 1;
  const result = await execute(m);
  assert.equal(result.code, 1);
  assert.equal(JSON.parse(result.stdout).status, 'TimedOut');
});
test('missing browser runtime executable records InfrastructureFailed', async () => {
  const result = await execute(manifest([step('goto', null, '/'), step('expectVisible', 'h1')]), { Quality__Execution__NodeExecutable: '/does-not-exist/quality-node' });
  assert.equal(result.code, 1);
  assert.equal(JSON.parse(result.stdout).status, 'InfrastructureFailed');
});

test('cancellation stops the browser process tree and persists Cancelled', async () => {
  const m = manifest([step('goto', null, '/'), step('expectVisible', '#never')]);
  const result = await execute(m, {}, undefined, 1800);
  assert.equal(result.code, 1, result.stderr);
  assert.equal(JSON.parse(result.stdout).status, 'Cancelled');
});
test('zero exit without a real report cannot claim a passing run', async () => {
  if (process.platform === 'win32') return;
  const result = await execute(manifest([step('goto', null, '/'), step('expectVisible', 'h1')]), { Quality__Execution__NodeExecutable: '/usr/bin/true' });
  assert.equal(result.code, 1);
  assert.equal(JSON.parse(result.stdout).status, 'InfrastructureFailed');
});

function processResult(command, args, env = process.env, cwd = root) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd, env });
    let stdout = '', stderr = '';
    child.stdout.on('data', data => stdout += data);
    child.stderr.on('data', data => stderr += data);
    child.on('error', reject);
    child.on('exit', code => resolve({ code, stdout, stderr }));
  });
}
test('passing reviewed tests produce an applicable Git patch and run as regressions', async () => {
  const previous = base;
  try {
    const created = await cli(['run', '--source', 'stub', '--reference', 'promotion-fixture']);
    const job = JSON.parse(created.stdout);
    // Fixture simulates a real normalized/planned job without contacting providers.
    job.requirement.isStub = false;
    job.testPlan.isStub = false;
    fs.writeFileSync(path.join(directory, 'jobs', job.id + '.json'), JSON.stringify(job));
    const draft = await cli(['prepare-execution', '--job', job.id, '--target', origin]);
    base = { job: job.id, manifest: JSON.parse(draft.stdout) };
    const result = await execute(manifest([step('goto', null, '/'), step('expectText', 'h1', 'Ready')]));
    assert.equal(result.code, 0, result.stderr);
    const run = JSON.parse(result.stdout);
    const proposed = await cli(['promote-regression', '--job', job.id, '--run', run.id, '--sha256', run.manifestHash]);
    assert.equal(proposed.code, 0, proposed.stderr);
    const patch = JSON.parse(proposed.stdout).patch;
    const destination = path.join(directory, 'patched');
    fs.mkdirSync(destination);
    assert.equal((await processResult('git', ['init', '--quiet'], process.env, destination)).code, 0);
    assert.equal((await processResult('git', ['apply', '--check', patch], process.env, destination)).code, 0);
    assert.equal((await processResult('git', ['apply', patch], process.env, destination)).code, 0);
    const regressions = path.join(destination, 'playwright', 'regressions');
    const stableId = fs.readdirSync(regressions)[0];
    assert.match(stableId, /^REG-[a-f0-9]{24}$/);
    const mapping = JSON.parse(fs.readFileSync(path.join(regressions, stableId, 'mapping.json')));
    assert.equal(mapping.runId, run.id);
    assert.equal(mapping.acceptanceCriteria[0].id, 'AC-1');
    const selected = JSON.parse(fs.readFileSync(path.join(regressions, stableId, 'manifest.json')));
    assert.deepEqual(selected.tests, manifest([step('goto', null, '/'), step('expectText', 'h1', 'Ready')]).tests);
    fs.cpSync(path.join(root, 'playwright', 'execution'), path.join(destination, 'playwright', 'execution'), { recursive: true });
    fs.cpSync(path.join(root, 'schemas'), path.join(destination, 'schemas'), { recursive: true });
    fs.copyFileSync(path.join(root, 'playwright', 'regressions.config.cjs'), path.join(destination, 'playwright', 'regressions.config.cjs'));
    fs.symlinkSync(path.join(root, 'node_modules'), path.join(destination, 'node_modules'), 'dir');
    const executed = await processResult(process.execPath, [require.resolve('@playwright/test/cli'), 'test', '--config', path.join(destination, 'playwright/regressions.config.cjs')],
      { ...process.env, QUALITY_REGRESSION_ORIGINS: origin }, destination);
    assert.equal(executed.code, 0, executed.stderr + executed.stdout);
    const denied = await processResult(process.execPath, [require.resolve('@playwright/test/cli'), 'test', '--config', path.join(destination, 'playwright/regressions.config.cjs')],
      { ...process.env, QUALITY_REGRESSION_ORIGINS: '' }, destination);
    assert.notEqual(denied.code, 0);
  } finally { base = previous; }
});

test('invalid test locator is classified as TestFailure, not an application regression', async () => {
  const result = await execute(manifest([step('goto', null, '/'), step('click', '['), step('expectText', 'h1', 'Ready')]));
  assert.equal(result.code, 1, result.stderr);
  const run = JSON.parse(result.stdout);
  assert.equal(run.status, 'Failed');
  assert.equal(run.failureClassification, 'TestFailure');
});
