import type { APIRequestContext } from '@playwright/test';
export type RequirementReference = { source: 'jira' | 'coda' | 'stub'; id: string };
export type JobStatus = 'Queued' | 'Normalizing' | 'Planning' | 'Completed' | 'Failed';
export interface QualityJob {
  id: string; status: JobStatus; reference: RequirementReference;
  requirement: { id: string; isStub: boolean } | null;
  testPlan: { requirementId: string; isStub: boolean; testCases: unknown[] } | null;
  transitions: { status: JobStatus; at: string; reason: string }[];
}
export class QualityClient {
  constructor(private readonly request: APIRequestContext) {}
  async submit(reference: RequirementReference): Promise<QualityJob> {
    const response = await this.request.post('/jobs', { data: { reference } });
    if (response.status() !== 202) throw new Error(`Submit failed: ${response.status()}`);
    return response.json();
  }
  async get(id: string): Promise<QualityJob> {
    const response = await this.request.get(`/jobs/${encodeURIComponent(id)}`);
    if (!response.ok()) throw new Error(`Get failed: ${response.status()}`);
    return response.json();
  }
}
