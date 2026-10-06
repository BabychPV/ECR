import { expect, test, type Page } from '@playwright/test';

/**
 * A2: F2 у СПРАВЖНЬОМУ `DocumentGrid` (RevoGrid 4.11) відкриває редактор поточної комірки.
 *
 * ⚠ До фіксу F2 у сітці документа не робив нічого (RevoGrid F2 не знає), а TESTER-SCENARIOS
 * п. «Клавіатура» його згадує. Тут - поведінка Excel: значення в полі, курсор у кінці, стрілки
 * рухають курсор у тексті (режим «відкрито Enter»), Esc скасовує, Enter/Tab зберігають; на
 * комірці лише для читання і в сітці лише для читання редактор не відкривається.
 *
 * ⚠ Стенд не потрібен (сервер у браузері, `DocumentGridGateStandPage`):
 * `ECR_E2E_OPTIONAL=1 npx playwright test e2e/gridF2Live.spec.ts`.
 */
const DocStand = '/_key-commit-gate/document';

const cellAt = (page: Page, col: number, row: number) =>
  page.locator(`revo-grid revogr-data[type="rgRow"] .rgCell[data-rgcol="${String(col)}"][data-rgrow="${String(row)}"]`);

const editorInput = (page: Page) => page.locator('revo-grid .edit-input-wrapper input');

async function openStand(page: Page, query = ''): Promise<void> {
  await page.goto(`${DocStand}${query}`);
  await expect(cellAt(page, 1, 0)).toBeVisible({ timeout: 30_000 });
  await cellAt(page, 1, 0).click();
  await page.waitForTimeout(300);
}

/** R1 QTY = `value`, фокус знову на R1 QTY (редактор закритий). */
async function seedFirstQty(page: Page, value: string): Promise<void> {
  await page.keyboard.type(value);
  await page.keyboard.press('Enter');
  await page.waitForTimeout(300);
  await page.keyboard.press('ArrowUp');
  await page.waitForTimeout(300);
  await expect(cellAt(page, 1, 0)).toHaveText(value);
  await expect(editorInput(page)).toHaveCount(0);
}

test.describe('F2 у сітці документа (A2)', () => {
  test('F2 відкриває редактор зі значенням і курсором у кінці; Enter зберігає дописане', async ({ page }) => {
    await openStand(page);
    await seedFirstQty(page, '12');

    await page.keyboard.press('F2');
    await expect(editorInput(page)).toBeFocused();
    await expect(editorInput(page)).toHaveValue('12');
    const caret = await editorInput(page).evaluate((input: HTMLInputElement) => [input.selectionStart, input.selectionEnd]);
    expect(caret).toEqual([2, 2]);

    await page.keyboard.type('3');
    await page.keyboard.press('Enter');
    await expect(cellAt(page, 1, 0)).toHaveText('123');
  });

  test('після F2 стрілка рухає курсор у тексті, а не фіксує', async ({ page }) => {
    await openStand(page);
    await seedFirstQty(page, '12');

    await page.keyboard.press('F2');
    await expect(editorInput(page)).toBeFocused();
    await page.keyboard.press('ArrowLeft');
    await page.keyboard.type('5');
    await expect(editorInput(page)).toHaveValue('152');
    await page.keyboard.press('Enter');
    await expect(cellAt(page, 1, 0)).toHaveText('152');
  });

  test('Esc після F2 скасовує правку', async ({ page }) => {
    await openStand(page);
    await seedFirstQty(page, '12');

    await page.keyboard.press('F2');
    await expect(editorInput(page)).toBeFocused();
    await page.keyboard.type('9');
    await page.keyboard.press('Escape');
    await expect(editorInput(page)).toHaveCount(0);
    await expect(cellAt(page, 1, 0)).toHaveText('12');
  });

  test('Tab після F2 зберігає', async ({ page }) => {
    await openStand(page);
    await seedFirstQty(page, '12');

    await page.keyboard.press('F2');
    await expect(editorInput(page)).toBeFocused();
    await page.keyboard.type('4');
    await page.keyboard.press('Tab');
    await expect(cellAt(page, 1, 0)).toHaveText('124');
  });

  test('символ одразу після F2 (без паузи) не губиться', async ({ page }) => {
    await openStand(page);
    await seedFirstQty(page, '12');

    await page.keyboard.press('F2');
    await page.keyboard.press('7');
    await page.keyboard.press('Enter');
    await expect(cellAt(page, 1, 0)).toHaveText('127');
  });

  test('комірка лише для читання: F2 редактор не відкриває', async ({ page }) => {
    await openStand(page);
    await cellAt(page, 0, 0).click();
    await page.waitForTimeout(300);

    await page.keyboard.press('F2');
    await page.waitForTimeout(400);
    await expect(editorInput(page)).toHaveCount(0);
    await expect(cellAt(page, 0, 0)).toHaveText('R1');
  });

  test('сітка лише для читання (закритий період): F2 редактор не відкриває', async ({ page }) => {
    await openStand(page, '?readonly=1');

    await page.keyboard.press('F2');
    await page.waitForTimeout(400);
    await expect(editorInput(page)).toHaveCount(0);
    // Контроль приладу: Enter тут так само нічого не відкриває.
    await page.keyboard.press('Enter');
    await page.waitForTimeout(400);
    await expect(editorInput(page)).toHaveCount(0);
  });
});
