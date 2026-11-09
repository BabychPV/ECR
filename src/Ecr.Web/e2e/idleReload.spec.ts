import { expect, test } from '@playwright/test';
import { signIn } from './multiSheet';

/**
 * ✎ debt-1109b (D-471, `tools/e2e-stand.ps1 -Grep "Idle reload:"`): сервер віддає `401 idleTimeout` лише на ПЕРШИЙ
 * запит із простроченим cookie й стирає cookie; перезавантаження далі отримує звичайний `401`, і вхід губив
 * «signed out after N min of inactivity».
 *
 * ⚠ Чекати справжні N хвилин бездіяльності в e2e не можна: ПЕРШУ відповідь `401 idleTimeout` підставляє маршрут
 * Playwright (рівно тіло, яке пише сервер, `IdleSession`), далі — справжній стенд (вихід і звичайний `401`).
 * ⛔ Без стенда файл ПАДАЄ (вхід), а не пропускається (honesty-guard).
 */
const Shots = process.env['ECR_E2E_SHEETNAME_SHOTS'];

test('Idle reload: after the server idle 401 a page reload still says why the user was signed out', async ({ page }) => {
  await signIn(page);

  // Перший запит із «простроченим» cookie — відповідь сервера про бездіяльність (один раз).
  // ⚠ Спершу піти зі сторінки застосунку: інакше idle-401 дістається фоновому запиту СТАРОЇ сторінки (опитування
  // задач/активності), що вивантажується, і перенаправлення не буде (перший прогін стенда 10.11).
  await page.goto('about:blank');

  // Перший `/me` нової сторінки — відповідь сервера про бездіяльність (один раз), як на перший запит із
  // простроченим cookie.
  let served = false;
  await page.route(/\/api\/v1\/me$/, async (route) => {
    if (served) {
      await route.continue();
      return;
    }
    served = true;
    await route.fulfill({
      status: 401,
      contentType: 'application/problem+json',
      body: JSON.stringify({
        title: 'Signed out after inactivity.',
        status: 401,
        errorCode: 'ECR-AUTH-0401',
        messageKey: 'err.ECR-AUTH-0401.idleTimeout',
        idleMinutes: 15,
        correlationId: 'e2e-idle',
      }),
    });
  });

  await page.goto('/');
  await page.waitForURL(/\/login\?.*reason=idle/, { timeout: 60_000 });

  // Сервер стер cookie: справжній вихід, далі будь-який запит — звичайний 401.
  await page.request.post('/api/v1/logout');
  await page.unroute(/\/api\/v1\/me$/);

  // «Перезавантаження» з адресою без причини — так її лишає звичайний 401 на старті застосунку.
  await page.goto('/login?from=%2F');
  const banner = page.locator('[data-login-reason="idle"]');
  await expect(banner).toBeVisible({ timeout: 30_000 });
  await expect(banner).toContainText('15');

  await page.screenshot({ path: test.info().outputPath('07-debt-b-idle-reload.png') });
  if (Shots !== undefined && Shots !== '') {
    await page.screenshot({ path: `${Shots}/07-debt-b-idle-reload.png` });
  }
});
