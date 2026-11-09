import { expect, test, type APIResponse } from '@playwright/test';
import { createMultiSheetDocument, signIn } from './multiSheet';

/**
 * Приймальна i4 проти РЕАЛЬНОГО стенда (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep "Periods reopened doc:"`):
 * «Projects → Periods tab: January shows "Locked · 1 document reopened until …" with the document name and a "Close now"
 * action».
 *
 * ⚠ Дані — через API: власний проєкт (`createMultiSheetDocument`, поточний рік за `ECR-Standard`, тож місяці, що вже
 * минули, закриті) і відкриття місяця для документа тим самим `POST …/period-reopen`, що й банер документа (#782).
 * ⛔ Без стенда файл ПАДАЄ (`globalSetup.ts`), а не пропускається (honesty-guard).
 */

interface CalendarPeriod {
  periodKey: number;
  state: string;
}

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

test.describe('Periods reopened doc', () => {
  test.describe.configure({ mode: 'serial', timeout: 300_000 });

  test('Periods reopened doc: a locked month shows the reopened document and Close now locks it again', async ({ page }) => {
    await signIn(page);
    const { documentId, periodKey: openKey } = await createMultiSheetDocument(page, 1, 1);
    const card = await ok<{ projectId: number }>(
      await page.request.get(`/api/v1/documents/${String(documentId)}?periodKey=${String(openKey)}`),
      'GET document',
    );
    const calendar = await ok<{ periods: CalendarPeriod[] }>(
      await page.request.get(`/api/v1/projects/${String(card.projectId)}/periods`),
      'календар проєкту',
    );
    const closed = calendar.periods.find((period) => period.state === 'Closed');
    expect(closed, 'у проєкту поточного року немає закритого місяця (січень мав бути закритий)').toBeDefined();
    const periodKey = closed?.periodKey ?? 0;

    await ok(
      await page.request.post(`/api/v1/documents/${String(documentId)}/period-reopen`, {
        data: { periodKey, reason: `e2e periods reopened ${String(Date.now())}`, openUntil: null },
      }),
      'відкриття місяця для документа',
    );
    const reopens = await ok<{ canClose: boolean; items: { documentId: number; documentName: string }[] }>(
      await page.request.get(`/api/v1/projects/${String(card.projectId)}/document-reopens`),
      'перелік винятків проєкту',
    );
    const item = reopens.items.find((entry) => entry.documentId === documentId);
    expect(item, 'відкритого документа немає в переліку проєкту').toBeDefined();
    expect(reopens.canClose).toBe(true);

    await page.goto(`/projects/${String(card.projectId)}/periods`);
    const row = page.locator(`[data-period-row="${String(periodKey)}"]`);
    await expect(row).toBeVisible({ timeout: 60_000 });
    await expect(row.locator('[data-period-state="Closed"]')).toBeVisible();

    const summary = row.getByTestId('period-reopened-summary');
    await expect(summary).toBeVisible({ timeout: 30_000 });
    // ⛔ Множина: «1 document», ніколи «1 documents».
    await expect(summary).toContainText(/^Locked · 1 document reopened until \S/);
    await expect(summary).not.toContainText('documents');

    const link = row.getByRole('link', { name: item?.documentName ?? '' });
    await expect(link).toHaveAttribute('href', `/documents/${String(documentId)}?periodKey=${String(periodKey)}`);

    await row.getByTestId('period-reopened-close').click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toContainText(item?.documentName ?? '');
    await dialog.getByTestId('confirm-verb').click();

    // Рядок знову просто «Locked» — без перезавантаження.
    await expect(summary).toHaveCount(0, { timeout: 30_000 });
    await expect(row.locator('[data-period-state="Closed"]')).toBeVisible();

    // Те саме — з боку сервера: виняток закрито, документ знову закритий.
    const state = await ok<{ active: unknown }>(
      await page.request.get(`/api/v1/documents/${String(documentId)}/period-reopen?periodKey=${String(periodKey)}`),
      'стан документа після Close now',
    );
    expect(state.active).toBeNull();
  });
});
