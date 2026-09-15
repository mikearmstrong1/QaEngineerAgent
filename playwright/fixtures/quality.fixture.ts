import { test as base, expect } from '@playwright/test';
import { QualityClient } from '../sdk/quality-client';
export const test = base.extend<{ quality: QualityClient }>({
  quality: async ({ request }, use) => { await use(new QualityClient(request)); },
});
export { expect };
