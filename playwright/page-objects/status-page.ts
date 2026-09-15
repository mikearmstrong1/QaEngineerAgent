import type { Page } from '@playwright/test';
export class StatusPage {
  constructor(private readonly page: Page) {}
  get heading() { return this.page.getByRole('heading', { name: 'Engineering Quality System' }); }
  get healthLink() { return this.page.getByRole('link', { name: 'Service health' }); }
  async open() { await this.page.goto('/'); }
}
