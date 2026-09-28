const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const source = fs.readFileSync(
  path.join(__dirname, '..', 'src', 'Quality.CommandCenter', 'wwwroot', 'app.js'),
  'utf8');
const html = fs.readFileSync(path.join(__dirname, '..', 'src', 'Quality.CommandCenter', 'wwwroot', 'index.html'), 'utf8');
const program = fs.readFileSync(path.join(__dirname, '..', 'src', 'Quality.CommandCenter', 'Program.cs'), 'utf8');
const compose = fs.readFileSync(path.join(__dirname, '..', 'docker-compose.yml'), 'utf8');

test('first-run workflow explains the review path', () => {
  assert.match(html, /aria-label="First-run workflow"/);
  for (const step of ['Generate', 'Review', 'Execute', 'Verify'])
    assert.match(html, new RegExp(`<strong>${step}</strong>`));
  assert.doesNotMatch(html, /Jira and OpenAI providers/);
});

test('planner configuration displays only provider and model metadata', () => {
  assert.match(program, /MapGet\("\/bff\/configuration"/);
  const endpoint = program.match(/MapGet\("\/bff\/configuration"[\s\S]*?\}\)\);/)?.[0] ?? '';
  assert.match(endpoint, /planningProvider = planningMode/);
  assert.match(endpoint, /planningModel/);
  assert.doesNotMatch(endpoint, /ApiKey|Endpoint|Token|Credential/i);
  assert.match(source, /Planner: /);
  assert.match(source, /loadPlannerConfiguration\(\)/);
});

test('Command Center receives safe planner display metadata only', () => {
  const commandCenter = compose.split('services:\n  command-center:')[1].split('\n  api:')[0];
  assert.match(commandCenter, /Quality__Planning__Mode/);
  assert.match(commandCenter, /Quality__Planning__Model/);
  assert.match(commandCenter, /Quality__Planning__Azure__Deployment/);
  assert.doesNotMatch(commandCenter, /OPENAI_API_KEY|ANTHROPIC_API_KEY|AZURE_OPENAI_API_KEY/);
});

test('evidence uses an explicit accessible disclosure control', () => {
  assert.match(source, /Show evidence files/);
  assert.match(source, /setAttribute\('aria-expanded','false'\)/);
  assert.match(source, /classList\.toggle\('hidden',!expanded\)/);
  assert.match(source, /aria-controls/);
});

test('manifest review leads with readable browser instructions', () => {
  assert.match(source, /function manifestReview/);
  assert.match(source, /Browser instructions/);
  assert.match(source, /Advanced: edit exact JSON/);
  assert.match(source, /Validate and save browser instructions/);
});

test('checksums are shortened visually and remain copyable', () => {
  assert.match(source, /function shortIdentifier/);
  assert.match(source, /navigator\.clipboard\.writeText/);
  assert.match(source, /identifier\('version checksum'/);
});
