import { test, expect } from '../fixtures/quality.fixture';
import { StatusPage } from '../page-objects/status-page';
import { smokeRequirement } from '../seeds/requirements';
import Ajv from 'ajv/dist/2020';
import addFormats from 'ajv-formats';
import fs from 'node:fs';
import path from 'node:path';
const serviceOrigin = process.env.QUALITY_BASE_URL ?? `http://127.0.0.1:${process.env.QUALITY_SMOKE_PORT ?? '5080'}`;

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
  expect(job.retryBudget?.workerAttempts).toBe(1);
  expect(job.retryBudget?.deadlineAt).toBeTruthy();
  expect(job.providerOperations?.map(operation => [operation.stage, operation.status, operation.attempts]))
    .toEqual([['Normalize', 'Completed', 1], ['Plan', 'Completed', 1]]);
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

test('submission retries reuse a job and conflicting keys return 409', async ({ request, quality }) => {
  const key = `smoke-${Date.now()}-${Math.random()}`;
  const submissions = await Promise.all(Array.from({ length: 12 }, () => quality.submit(smokeRequirement, key)));
  expect(new Set(submissions.map(job => job.id)).size).toBe(1);
  const id = submissions[0].id;
  await expect.poll(async () => (await quality.get(id)).status).toBe('Completed');
  expect((await quality.submit(smokeRequirement, key)).status).toBe('Completed');
  const conflict = await request.post('/jobs', {
    headers: { 'Idempotency-Key': key }, data: { reference: { source: 'stub', id: 'different' } },
  });
  expect(conflict.status()).toBe(409);
  expect(await conflict.json()).toEqual({ error: 'idempotency_key_conflict' });
  expect((await request.post('/jobs', {
    headers: { 'Idempotency-Key': 'invalid key' }, data: { reference: smokeRequirement },
  })).status()).toBe(400);
});


test('completed plans reject cancellation and retain their result', async ({ request, quality }) => {
  const job = await quality.submit(smokeRequirement);
  await expect.poll(async () => (await quality.get(job.id)).status).toBe('Completed');
  const before = await quality.get(job.id);
  const response = await request.post(`/jobs/${job.id}/cancel`);
  expect(response.status()).toBe(409);
  expect((await response.json()).error).toBe('job_already_terminal');
  await expect(quality.cancel(job.id)).rejects.toThrow('Cancel failed: 409');
  expect(await quality.get(job.id)).toEqual(before);
  expect((await request.post('/jobs/not-an-id/cancel')).status()).toBe(400);
  expect((await request.post('/jobs/00000000000000000000000000000000/cancel')).status()).toBe(404);
});

test('manifest explanation is read-only and reports traceable coverage gaps', async ({ request, quality }) => {
  const job = await quality.submit(smokeRequirement);
  await expect.poll(async () => (await quality.get(job.id)).status).toBe('Completed');
  const created = await request.post(`/jobs/${job.id}/execution-requests`, { data: { target: serviceOrigin } });
  expect(created.status()).toBe(201);
  const execution = await created.json();
  const explained = await request.post(`/execution-requests/${execution.id}/explain-manifest`);
  const result = await explained.json();
  expect(explained.status(), JSON.stringify(result)).toBe(200);
  expect(result.complete).toBe(false);
  expect(result.gaps.length).toBeGreaterThan(0);
  expect(result.tests[0].steps[0].testCaseId).toBe('TC-1');
  expect(result.manifest.tests[0].steps.some((step: { selector?: string }) => step.selector === 'body')).toBe(false);
  const saved = await request.get(`/execution-requests/${execution.id}`);
  expect((await saved.json()).status).toBe('Draft');
});
