import { expect, test, type Page } from '@playwright/test';

/**
 * L8-08, відповідь «Піти»: збереження впало (`500`), правка лишилась у сховищі; закриття вкладки питає
 * «Покинути сторінку?» (без маячка - інакше «Залишитися» дало б `409` на власній правці). Людина обрала
 * «Піти» - правка МАЄ дійти до сервера маячком із `pagehide`. Доказ - нова вкладка бачить значення.
 *
 * ⚠ Стенд готує `tools/e2e-stand.ps1`; без нього набір пропускається так само, як `gridPasteAfterEditor.spec.ts`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

test.describe('«Піти» у beforeunload (L8-08)', () => {
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );

  async function openGrid(page: Page): Promise<void> {
    await page.goto('/login');
    await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
    await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
    await page.keyboard.press('Enter');
    await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
    await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
    await expect(page.locator('revo-grid').first().locator('revogr-data .rgCell').first()).toBeVisible({
      timeout: 30_000,
    });
  }

  const cellOf = (page: Page, row: number) =>
    page.locator('revo-grid').first().locator('revogr-data[type="rgRow"] .rgCell').nth(row);

  test('«Піти» після збою збереження: правка доїжджає маячком із pagehide', async ({ page, context }) => {
    await openGrid(page);

    // Усі PATCH падають (`500`) - правка лишається у сховищі «придатною до надсилання».
    const failed = page.waitForResponse(
      (response) => response.request().method() === 'PATCH' && response.url().includes(`/documents/${DocumentId}`),
      { timeout: 15_000 },
    );
    let leaving = false;
    await page.route(`**/api/v1/documents/${DocumentId}/cells`, (route) =>
      leaving ? route.continue() : route.fulfill({ status: 500, contentType: 'application/json', body: '{}' }),
    );

    const value = String(100 + Math.floor(Math.random() * 800));
    await cellOf(page, 0).click();
    const editor = page.locator('revo-grid').first().locator('.edit-input-wrapper input');
    await page.keyboard.press('Enter');
    await expect(editor).toBeFocused();
    await page.keyboard.type(value);
    await page.keyboard.press('Enter');
    await expect(page.locator('.edit-input-wrapper')).toHaveCount(0);
    expect((await failed).status()).toBe(500);

    // ⚠ `close({ runBeforeUnload })` не чекає на діалог: ловимо подію, відповідаємо «Піти» й чекаємо закриття.
    const dialogShown = page.waitForEvent('dialog', { timeout: 15_000 });
    const closed = page.waitForEvent('close', { timeout: 30_000 });
    await page.close({ runBeforeUnload: true });
    const dialog = await dialogShown;
    expect(dialog.type(), 'закриття вкладки з невдалим збереженням має спитати «Покинути сторінку?»').toBe(
      'beforeunload',
    );
    // ⚠ Чесно про силу доказу: звичайний збір (`setTimeout 0`) після діалогу в Chromium/Playwright ще
    // встигає до вивантаження, тож ЦЕЙ тест зелений і без `pagehide`-маячка (перевірено локально). Він
    // страхує наскрізний шлях «Піти -> правка на сервері»; відсутність маячка доводять vitest-и
    // `autosave.leavePagehide.test.tsx` (червоні без фіксу).
    leaving = true;
    await dialog.accept();
    await closed;

    // Нова вкладка: значення на сервері.
    const fresh = await context.newPage();
    await expect(async () => {
      await fresh.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
      await expect(cellOf(fresh, 0)).toBeVisible({ timeout: 15_000 });
      await expect(cellOf(fresh, 0)).toContainText(value, { timeout: 2_000 });
    }).toPass({ timeout: 30_000 });
  });
});
