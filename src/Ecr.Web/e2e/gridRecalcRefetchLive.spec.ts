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
