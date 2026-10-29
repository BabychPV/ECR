import { expect, test, type Page } from '@playwright/test';

/**
 * D-1 / CL-01: після правки комірки обчислювана колонка оновлюється БЕЗ F9 і без перезавантаження.
 *
 * Стенд (`DocumentGridGateStandPage`, `?calc=1`) моделює сервер: TOTAL = 2 × QTY відстає від правки
 * і з'являється в зрізі лише після завершення задачі перерахунку. До фіксу сітка не перезапитувала
 * зріз і показувала порожній TOTAL, хоча рядок статусу казав «перераховано».
 *
 * `ECR_E2E_OPTIONAL=1 npx playwright test e2e/gridRecalcRefetchLive.spec.ts`
 */
const cellAt = (page: Page, col: number, row: number) =>
  page.locator(`revo-grid revogr-data[type="rgRow"] .rgCell[data-rgcol="${String(col)}"][data-rgrow="${String(row)}"]`);

/** Скільки разів сітка запитала зріз таблиці (GET `/tables/{id}` без хвоста). */
function countSliceGets(page: Page): { count: () => number } {
  let n = 0;
  page.on('request', (r) => {
    if (r.method() === 'GET' && /\/api\/v1\/documents\/\d+\/tables\/\d+(\?|$)/.test(r.url())) n += 1;
  });
  return { count: () => n };
}

async function editQty(page: Page, url: string): Promise<void> {
  await page.goto(url);
  await expect(cellAt(page, 1, 0)).toBeVisible({ timeout: 30_000 });
  await expect(cellAt(page, 2, 0)).toHaveText('');
  await cellAt(page, 1, 0).click();
  await page.waitForTimeout(300);
  await page.keyboard.type('7');
  await page.keyboard.press('Enter');
  await expect(cellAt(page, 1, 0)).toHaveText('7');
}

// L3: задача формул одна на документ і пише в кілька періодів (`JobStatus.writtenPeriods`).
test('L3: записані періоди містять період правки → зріз перечитано, TOTAL з\'явився', async ({ page }) => {
  await editQty(page, '/_key-commit-gate/document?calc=periods');
  await expect(page.locator('[data-recalc-status="succeeded"]')).toBeVisible({ timeout: 15_000 });
  await expect(cellAt(page, 2, 0)).toHaveText('14', { timeout: 10_000 });
});

test('L3: записано лише в інший період → зріз періоду правки не перечитується', async ({ page }) => {
  const gets = countSliceGets(page);
  await editQty(page, '/_key-commit-gate/document?calc=other');
  const before = gets.count();
  await expect(page.locator('[data-recalc-status="succeeded"]')).toBeVisible({ timeout: 15_000 });
  await page.waitForTimeout(2_000);
  expect(gets.count()).toBe(before);
  await expect(cellAt(page, 2, 0)).toHaveText('');
});

test('правка QTY → залежний TOTAL змінюється без F9', async ({ page }) => {
  await page.goto('/_key-commit-gate/document?calc=1');
  await expect(cellAt(page, 1, 0)).toBeVisible({ timeout: 30_000 });
  await expect(cellAt(page, 2, 0)).toBeVisible();

  // Початковий стан: обчислюване значення ще не пораховане.
  await expect(cellAt(page, 2, 0)).toHaveText('');

  await cellAt(page, 1, 0).click();
  await page.waitForTimeout(300);
  await page.keyboard.type('7');
  await page.keyboard.press('Enter');

  // Правка збережена, перерахунок іще йде: TOTAL поки старий (порожній) — не вакуумний «початок».
  await expect(cellAt(page, 1, 0)).toHaveText('7');
  await expect(cellAt(page, 2, 0)).toHaveText('');

  // Кінець: задача завершилась → сітка сама перечитала зріз.
  await expect(page.locator('[data-recalc-status="succeeded"]')).toBeVisible({ timeout: 15_000 });
  await expect(cellAt(page, 2, 0)).toHaveText('14', { timeout: 10_000 });
});
