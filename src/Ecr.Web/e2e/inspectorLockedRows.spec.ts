import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { createMultiSheetDocument, signIn } from './multiSheet';

/**
 * ✎ 12.11 (людина, doc 96 жовтень 2026, T3_00) проти РЕАЛЬНОГО стенда (`tools/e2e-stand.ps1`, workflow `e2e-stand` за
 * ім'ям файлу або `-g "Locked rows:"`): пункт закритого місяця в Issues → «Rows that need attention».
 *  - одне коротке речення на рядок: «Row <код рядка>: the <місяць> columns are in a closed month and can't be edited
 *    here.» — без сирих заголовків колонок (вони — у розгортанні «Locked columns»);
 *  - клік по пункту веде в рядок тим самим шляхом, що й пункт перевірки: інспектор показує адресу вибраної комірки
 *    цього рядка (`[data-inspector-address]`);
 *  - «Reopen <місяць> for this document…» — для того, кому сервер дозволяє відкриття (`canOpen`): оператор стенда має
 *    `Period.Reopen` у своєму проєкті (той самий, що й у `documentPeriodReopen.spec.ts`).
 *
 * Свої дані (serial describe, власне налаштування): проєкт і документ (`createMultiSheetDocument`, проєкт поточного
 * року за `ECR-Standard`, тож минулі місяці закриті), у закритому місяці — рядок зі значенням: місяць тимчасово
 * відкривається ЛИШЕ для цього документа (`POST …/period-reopen`), рядок і значення пишуться, виняток закривається
 * (`DELETE …/period-reopen`) — місяць знову закритий, рядок у ньому лишається. Базову лінію стенда файл не чіпає.
 *
 * ⛔ Без стенда файл ПАДАЄ (`globalSetup.ts`), а не пропускається (honesty-guard).
 * ⚠ Написано без локального прогону (правило «e2e — на CI»): перший живий прогін — CI.
 */

interface CalendarPeriod {
  periodKey: number;
  state: string;
}

interface SliceRow {
  rowKey: string;
  rowVersion: string;
}

const MonthNames = [
  'January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December',
];

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

/** Документ із рядком у закритому місяці. */
async function rowInClosedMonth(page: Page): Promise<{ documentId: number; periodKey: number; rowKey: string }> {
  const { documentId, projectId } = await createMultiSheetDocument(page, 1, 1);
  const calendar = await ok<{ periods: CalendarPeriod[] }>(
    await page.request.get(`/api/v1/projects/${String(projectId)}/periods`),
    'календар проєкту',
  );
  const closed = calendar.periods.find((period) => period.state === 'Closed');
  expect(closed, 'у проєкту поточного року немає закритого місяця (січень мав бути закритий)').toBeDefined();
  const periodKey = closed?.periodKey ?? 0;

  // Місяць — тимчасово відкритий лише для цього документа: рядок і значення в ньому.
  await ok(
    await page.request.post(`/api/v1/documents/${String(documentId)}/period-reopen`, {
      data: { periodKey, reason: 'e2e locked rows: arrange a row in a locked month', openUntil: null },
    }),
    'тимчасове відкриття місяця',
  );
  const tables = await ok<{ tableInstanceId: number }[]>(
    await page.request.get(`/api/v1/documents/${String(documentId)}/tables?periodKey=${String(periodKey)}`),
    'таблиці документа',
  );
  const tableInstanceId = tables[0]?.tableInstanceId ?? 0;
  await ok(
    await page.request.post(`/api/v1/documents/${String(documentId)}/rows`, { data: { tableInstanceId, rowKey: null } }),
    'рядок',
  );
  const slice = await ok<{ rows: SliceRow[] }>(
    await page.request.get(`/api/v1/documents/${String(documentId)}/tables/${String(tableInstanceId)}`),
    'зріз',
  );
  const row = slice.rows[0];
  expect(row, 'рядок не з’явився у зрізі').toBeDefined();
  await ok(
    await page.request.patch(`/api/v1/documents/${String(documentId)}/cells`, {
      data: {
        tableInstanceId,
        periodKey,
        origin: 'UserEdit',
        rows: [{ rowKey: row?.rowKey ?? '', baseVersion: row?.rowVersion ?? '', cells: [{ columnCode: 'C1', value: 'e2e', isEmpty: false }] }],
      },
    }),
    'значення рядка',
  );
  // Виняток закрито — місяць знову закритий, рядок у ньому лишається.
  await ok(
    await page.request.delete(`/api/v1/documents/${String(documentId)}/period-reopen?periodKey=${String(periodKey)}`),
    'закриття винятку',
  );

  return { documentId, periodKey, rowKey: row?.rowKey ?? '' };
}

test.describe('Locked rows', () => {
  test.describe.configure({ mode: 'serial', timeout: 300_000 });

  test('Locked rows: one sentence per row with the month name, click selects the row, reopen action for an allowed user', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);
    const { documentId, periodKey, rowKey } = await rowInClosedMonth(page);
    const month = MonthNames[(periodKey % 100) - 1] ?? '';

    await page.goto(`/documents/${String(documentId)}?periodKey=${String(periodKey)}&panel=issues`);
    await expect(page.locator('[data-document-lock="periodClosed"]')).toBeVisible({ timeout: 60_000 });
    const item = page.locator(`[data-inspector-locked-row$=":${rowKey}:locked"]`);
    await expect(item).toBeVisible({ timeout: 60_000 });

    // Одне коротке речення: рядок, місяць назвою; без сирих заголовків колонок і без старого речення таблиці.
    await expect(item).toContainText(`Row ${rowKey}`);
    await expect(item).toContainText(`the ${month} columns are in a closed month and can't be edited here.`);
    await expect(item).not.toContainText('values there are read-only');
    await expect(page.locator('[data-inspector-locked]')).toHaveCount(0);
    // Колонки — лише в розгортанні.
    const details = page.locator(`[data-inspector-locked-columns$=":${rowKey}:locked"]`);
    await expect(details.locator('summary')).toHaveText('Locked columns');

    // Клік веде в рядок: інспектор показує адресу вибраної комірки цього рядка.
    await item.click();
    await expect(page.locator('[data-inspector-address]')).toContainText(rowKey, { timeout: 30_000 });

    // Дія відкриття — для того, кому сервер дозволяє (оператор має Period.Reopen у своєму проєкті).
    const reopen = page.locator(`[data-inspector-reopen="${String(periodKey)}"]`);
    await expect(reopen).toBeVisible({ timeout: 30_000 });
    await expect(reopen).toContainText(/for this document/i);
    await expect(page.locator('[data-inspector-reopen-denied]')).toHaveCount(0);
  });
});
