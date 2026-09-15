import { test, expect } from '../fixtures/quality.fixture';
import { StatusPage } from '../page-objects/status-page';
import { smokeRequirement } from '../seeds/requirements';
import Ajv from 'ajv/dist/2020';
import addFormats from 'ajv-formats';
import fs from 'node:fs';
import path from 'node:path';

test('browser renders the local service and follows the health link', async ({ page }) => {
  const status = new StatusPage(page);
  await status.open();
  await expect(status.heading).toBeVisible();
  await status.healthLink.click();
  await expect(page.locator('body')).toContainText('"status":"ok"');
});

test('requirement becomes a persisted, schema-valid plan', async ({ quality }) => {
  const submitted = await quality.submit(smokeRequirement);
  expect(submitted.status).toBe('Queued');
  await expect.poll(async () => (await quality.get(submitted.id)).status).toBe('Completed');
  const job = await quality.get(submitted.id);
  expect(job.requirement?.isStub).toBe(true);
  expect(job.testPlan?.requirementId).toBe(job.requirement?.id);
  expect(job.testPlan?.testCases).toHaveLength(1);
  expect(job.transitions.map(t => t.status)).toEqual(['Queued', 'Normalizing', 'Planning', 'Completed']);
  const ajv = new Ajv({ allErrors: true, strict: true });
  addFormats(ajv);
  const schemas = path.resolve(__dirname, '../../schemas/v1');
  for (const name of fs.readdirSync(schemas).filter(n => n.endsWith('.schema.json')))
    ajv.addSchema(JSON.parse(fs.readFileSync(path.join(schemas, name), 'utf8')));
  const validate = ajv.getSchema('https://quality.local/schemas/v1/job.schema.json')!;
  expect(validate(job), JSON.stringify(validate.errors)).toBe(true);
});

test('invalid input and unknown jobs have explicit HTTP responses', async ({ request }) => {
  expect((await request.post('/jobs', { data: { reference: { source: 'jira', id: 'bad' } } })).status()).toBe(400);
  expect((await request.post('/jobs', { data: {} })).status()).toBe(400);
  expect((await request.post('/jobs', { data: { reference: { source: 'stub', id: 'ok' }, extra: true } })).status()).toBe(400);
  expect((await request.get('/jobs/not-an-id')).status()).toBe(400);
  expect((await request.get('/jobs/00000000000000000000000000000000')).status()).toBe(404);
});
