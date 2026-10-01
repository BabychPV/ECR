import { expect, test } from '@playwright/test';

/**
 * Сітка 500×60 малює лише видиме вікно, а не 30 000 комірок (`ФВ-14.29`).
 *
 * ⛔ Чому в браузері, а не у vitest. Віртуалізація — це властивість РОЗКЛАДКИ:
 * скільки рядків уміщує слот 70vh. У jsdom розкладки немає (усі розміри нулі),
 * а RevoGrid у юніт-тестах — заглушка, тож юніт-тест довів би хіба те, що
 * заглушка нічого не малює. Тут — справжній RevoGrid із параметрами
 * `DocumentGrid` на сторінці-стенді `/_virtual-grid` (DEV-маршрут, стенд БД
 * не потрібен — як `cellStates.spec.ts`; запуск без стенда:
 * `ECR_E2E_OPTIONAL=1 npx playwright test e2e/virtualGrid.spec.ts`).
 *
 * ⚠ Стеля — не «менше 30 000», а конкретне число з запасом: екран 1280×720
 * при слоті 70vh уміщує ~20 рядків × ~11 колонок; RevoGrid домальовує буфер
 * з обох боків. 2 000 — це вдесятеро більше за видиме, тобто межа ловить
 * ВИМКНЕНУ віртуалізацію (30 000) і не чіпляється до буфера, що трохи змінився.
 */
const Rows = 500;
const Columns = 60;
const MountedCellsCeiling = 2_000;

test('ФВ-14.29: сітка 500×60 віртуалізована — у DOM лише вікно, кінець досяжний прокруткою', async ({ page }) => {
  await page.goto('/_virtual-grid');

  const stand = page.locator('[data-measure="virtual-grid"]');
  await expect(stand).toHaveAttribute('data-rows', String(Rows));
  await expect(stand).toHaveAttribute('data-columns', String(Columns));
  await expect(page.getByText('r1c1', { exact: true })).toBeVisible({ timeout: 30_000 });

  const mounted = await stand.locator('.rgCell').count();
  console.log(`virtual-grid: змонтовано комірок ${mounted} із ${Rows * Columns}`);

  expect(mounted).toBeGreaterThan(0);
  expect(mounted).toBeLessThan(MountedCellsCeiling);

  // ⛔ Останнього рядка в DOM на старті немає — саме це й означає «віртуалізовано».
  await expect(page.getByText(`r${Rows}c1`, { exact: true })).toHaveCount(0);

  // Прокрутка до кінця: останній рядок з'являється, комірок у DOM не більшає.
  await stand.locator('revo-grid').evaluate(async (grid, last) => {
    await (grid as unknown as { scrollToRow: (row: number) => Promise<void> }).scrollToRow(last);
  }, Rows - 1);

  await expect(page.getByText(`r${Rows}c1`, { exact: true })).toBeVisible({ timeout: 10_000 });
  expect(await stand.locator('.rgCell').count()).toBeLessThan(MountedCellsCeiling);
});
