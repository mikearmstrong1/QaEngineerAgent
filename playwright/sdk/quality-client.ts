import type { APIRequestContext } from '@playwright/test';
export type RequirementReference = { source: 'jira' | 'coda' | 'stub'; id: string };
export type JobStatus = 'Queued' | 'Normalizing' | 'Planning' | 'Completed' | 'Failed' | 'Cancelled';
export interface ProviderOperation {
  id: string; stage: 'Normalize' | 'Plan'; provider: string; inputHash: string;
  status: 'Started' | 'Completed' | 'Failed' | 'OutcomeUnknown' | 'Cancelled'; attempts: number;
  startedAt: string; finishedAt: string | null; error: string | null; retryAt?: string | null;
}
export interface RetryBudget {
  maxWorkerAttempts: number; maxNormalizationAttempts: number; maxDurationSeconds: number;
  initialDelayMilliseconds: number; maxDelayMilliseconds: number; workerAttempts: number;
  deadlineAt: string | null; lastClaimedAt: string | null;
}
export interface QualityJob {
  id: string; status: JobStatus; reference: RequirementReference;
  providerOperations?: ProviderOperation[] | null;
  retryBudget?: RetryBudget | null;
  requirement: { id: string; isStub: boolean } | null;
  testPlan: { requirementId: string; isStub: boolean; testCases: unknown[] } | null;
  transitions: { status: JobStatus; at: string; reason: string }[];
}
export class QualityClient {
  constructor(private readonly request: APIRequestContext) {}
  async submit(reference: RequirementReference, idempotencyKey?: string): Promise<QualityJob> {
    const response = await this.request.post('/jobs', { data: { reference }, headers: idempotencyKey === undefined ? {} : { 'Idempotency-Key': idempotencyKey } });
    if (response.status() !== 202) throw new Error(`Submit failed: ${response.status()}`);
    return response.json();
  }
  async cancel(id: string): Promise<QualityJob> {
    const response = await this.request.post(`/jobs/${encodeURIComponent(id)}/cancel`);
    if (response.status() !== 200) throw new Error(`Cancel failed: ${response.status()}`);
    return response.json();
  }
  async get(id: string): Promise<QualityJob> {
    const response = await this.request.get(`/jobs/${encodeURIComponent(id)}`);
    if (!response.ok()) throw new Error(`Get failed: ${response.status()}`);
    return response.json();
  }
}
