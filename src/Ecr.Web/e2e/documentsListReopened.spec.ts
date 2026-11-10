import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { addDocument, createMultiSheetDocument, signIn } from './multiSheet';

/**
 * ✎ 10.11 (слово людини через координатора): закритий місяць, відкритий для ОДНОГО документа, — у переліку документів.
 * Приймальна проти РЕАЛЬНОГО стенда (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep "Documents list reopened:"`):
 * відкрити січень для A → у смузі A січень «Reopened for this document until …» (відкритий замок), у B — «Locked»;
 * фільтр «Reopened months» лишає лише A; закрити виняток → січень A знову «Locked».
 *
 * ⚠ Дані — через API: власний проєкт поточного року (`ECR-Standard`, тож січень уже закритий) і два документи; відкриття —
 * тим самим `POST …/period-reopen`, що й банер документа, закриття — `DELETE …/period-reopen` («Close now»).
 * ⛔ Без стенда файл ПАДАЄ (`globalSetup.ts`), а не пропускається (honesty-guard).
 */

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

async function businessKeyOf(page: Page, documentId: number, periodKey: number): Promise<string> {
  const card = await ok<{ businessKey: string }>(
    await page.request.get(`/api/v1/documents/${String(documentId)}?periodKey=${String(periodKey)}`),
    'GET document',
  );

  return card.businessKey;
}

test.describe('Documents list reopened', () => {
  test.describe.configure({ mode: 'serial', timeout: 300_000 });

  for (const scheme of ['light', 'dark'] as const) {
    test(`Documents list reopened: month reopened for doc A shows only in A's strip, the filter keeps A, closing locks it again (${scheme})`, async ({
      page,
    }) => {
      await page.emulateMedia({ colorScheme: scheme });
      await signIn(page);
      const a = await createMultiSheetDocument(page, 1, 1);
      const bId = await addDocument(page, a);
      const calendar = await ok<{ periods: { periodKey: number; state: string }[] }>(
        await page.request.get(`/api/v1/projects/${String(a.projectId)}/periods`),
        'календар проєкту',
      );
      const closed = calendar.periods.find((period) => period.state === 'Closed');
      expect(closed, 'у проєкту поточного року немає закритого місяця (січень мав бути закритий)').toBeDefined();
      const month = closed?.periodKey ?? 0;
      const year = Math.trunc(month / 100);
      const keyA = await businessKeyOf(page, a.documentId, a.periodKey);
      const keyB = await businessKeyOf(page, bId, a.periodKey);

      await ok(
        await page.request.post(`/api/v1/documents/${String(a.documentId)}/period-reopen`, {
          data: { periodKey: month, reason: `e2e list reopened ${String(Date.now())}`, openUntil: null },
        }),
        'відкриття місяця для документа A',
      );

      await page.setViewportSize({ width: 1440, height: 900 });
      await page.goto(`/?projectId=${String(a.projectId)}&year=${String(year)}`);
      const rowA = page.locator(`[data-document-row="${keyA}"]`);
      const rowB = page.locator(`[data-document-row="${keyB}"]`);
      await expect(rowA).toBeVisible({ timeout: 60_000 });
      await expect(rowB).toBeVisible();

      const cellA = rowA.locator(`[data-month="${String(month)}"]`);
      const cellB = rowB.locator(`[data-month="${String(month)}"]`);
      await expect(cellA).toHaveAttribute('data-cell', 'reopened', { timeout: 30_000 });
      await expect(cellA).toHaveAttribute('aria-label', /Reopened for this document until \S/);
      await expect(cellA.locator('[data-mark="open-lock"]')).toBeVisible();
      await expect(cellB).toHaveAttribute('data-cell', 'locked');

      const legend = page.locator('[data-year-strip-legend]');
      await expect(legend).toContainText('Reopened for this document');
      await rowA.locator('[data-column="months"]').screenshot({ path: test.info().outputPath(`list-reopened-strip-${scheme}.png`) });
      await legend.screenshot({ path: test.info().outputPath(`list-reopened-legend-${scheme}.png`) });

      // Той самий 18×18 і та сама лінія, що в сусідів (KIT.md «MonthStrip»).
      const boxes = await rowA.locator('[data-month] .ecr-year-strip-box').evaluateAll((nodes) =>
        nodes.map((node) => {
          const rect = node.getBoundingClientRect();
          return { w: rect.width, h: rect.height, top: rect.top };
        }),
      );
      for (const box of boxes) {
        expect(Math.abs(box.w - 18), JSON.stringify(boxes)).toBeLessThanOrEqual(0.5);
        expect(Math.abs(box.h - 18), JSON.stringify(boxes)).toBeLessThanOrEqual(0.5);
        expect(Math.abs(box.top - (boxes[0]?.top ?? 0)), JSON.stringify(boxes)).toBeLessThanOrEqual(0.5);
      }

      // Фільтр «Reopened months» — лише A; адреса несе його, «Clear filters» знімає.
      // ⚠ Вхід Mantine Switch візуально схований (поза областю) — клікаємо підпис, як людина.
      const toggle = page.getByRole('switch', { name: 'Reopened months' });
      await page.locator('label', { hasText: 'Reopened months' }).first().click();
      await expect(toggle).toBeChecked();
      await expect(page).toHaveURL(/hasReopenedMonths=true/);
      await expect(rowA).toBeVisible({ timeout: 30_000 });
      await expect(rowB).toHaveCount(0, { timeout: 30_000 });
      await page.locator('[data-document-filters="true"]').screenshot({ path: test.info().outputPath(`list-reopened-filter-${scheme}.png`) });

      // Закрити виняток («Close now») → січень A знову «Locked», фільтр — порожній.
      await ok(
        await page.request.delete(`/api/v1/documents/${String(a.documentId)}/period-reopen?periodKey=${String(month)}`),
        'закриття винятку',
      );
      await page.reload();
      await expect(rowA).toHaveCount(0, { timeout: 30_000 });
      await page.locator('[data-clear-filters]').first().click();
      await expect(page).not.toHaveURL(/hasReopenedMonths/);
      await expect(rowA.locator(`[data-month="${String(month)}"]`)).toHaveAttribute('data-cell', 'locked', { timeout: 30_000 });
      await expect(rowB.locator(`[data-month="${String(month)}"]`)).toHaveAttribute('data-cell', 'locked');
    });
  }
});
