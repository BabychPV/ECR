import { expect, test, type APIRequest, type APIRequestContext, type Page } from '@playwright/test';
import path from 'node:path';

/**
 * D-471 «Idle sign-out:» — вихід після бездіяльності на ЖИВОМУ стенді (`tools/e2e-stand.ps1 -Grep "Idle sign-out:"`).
 *
 * ⚠ Межа — 1 хв: стенд знижує мінімум конфігурацією (`ECR_Auth__IdleTimeout__MinimumMinutes=1`, лише
 * `e2e-stand.ps1`); у продукті мінімум 5. На 1 хв попередження — за 30 с (`warningLeadMs`). Перший прогін
 * ставить 1 хв через екран «Security → Session», `afterAll` повертає 30 — решта набору живе з типовим значенням.
 *
 * id1 — вкладка, що лише опитує, виходить на межі, і це тримає СЕРВЕР (`401 idleTimeout` на опитування);
 * id2 — клацання й клавіша скидають відлік; id3 — попередження за 30 с і «Stay signed in»;
 * id4 — друга вкладка живе від дій першої і виходить разом із нею; id5 — налаштування зберігається, журналюється
 * і діє на сеанс, відкритий ДО зміни; id6 — рядок журналу `SignedOut` з причиною `idle`.
 *
 * ⛔ Без стенда файл ПАДАЄ (`beforeAll`), а не пропускається (honesty-guard).
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const ShotsDir = process.env['ECR_E2E_SHOTS'] ?? '';
const IdleRe = /signed out after 1 min|после 1 мин|1 мин әрекетсіздіктен/i;

async function shot(page: Page, name: string): Promise<void> {
  if (ShotsDir === '') return;
  await page.screenshot({ path: path.join(ShotsDir, `${name}.png`), fullPage: false });
}

async function apiLogin(api: APIRequestContext): Promise<void> {
  const response = await api.post('/api/v1/login/local', { data: { userName: Admin.user, password: Admin.password } });
  expect(response.ok(), `вхід ${Admin.user}: ${String(response.status())}`).toBe(true);
}

async function newApi(request: APIRequest, baseURL: string | undefined): Promise<APIRequestContext> {
  const api = await request.newContext(baseURL === undefined ? {} : { baseURL });
  await apiLogin(api);
  return api;
}

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я|Имя/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

/** Тиша на сторінці: жодних дій людини, лише опитування самого застосунку. */
async function idle(page: Page, ms: number): Promise<void> {
  await page.waitForTimeout(ms);
}

test.describe('Idle sign-out:', () => {
  test.describe.configure({ mode: 'serial', timeout: 240_000 });

  let baseURL: string | undefined;
  let poller: APIRequestContext;
  let pollerLoggedInAt = 0;

  test.beforeAll(async ({ playwright }, info) => {
    baseURL = info.project.use.baseURL;
    // id5: сеанс відкрито ДО зміни налаштування — межа має дійти й до нього.
    poller = await newApi(playwright.request, baseURL);
    pollerLoggedInAt = Date.now();
  });

  test.afterAll(async ({ playwright }) => {
    await poller.dispose();
    const api = await newApi(playwright.request, baseURL);
    const response = await api.put('/api/v1/security/session-settings', { data: { idleTimeoutMinutes: 30 } });
    expect(response.status(), 'повернути 30 хв').toBe(200);
    await api.dispose();
  });

  test('Idle sign-out: id5 setting saves, is audited and reaches /me', async ({ page }) => {
    await page.setViewportSize({ width: 1335, height: 860 });
    await signIn(page);
    await page.goto('/admin/security?tab=session');

    const field = page.getByLabel(/Sign out after N min without activity/i);
    await expect(field).toBeVisible({ timeout: 30_000 });
    await field.fill('1');
    await page.getByRole('button', { name: /^Save$/ }).click();
    await expect(page.getByText(/Sign-out after inactivity: 1 min/i)).toBeVisible({ timeout: 30_000 });
    await shot(page, 'id5-setting-light-1335');

    await page.setViewportSize({ width: 375, height: 812 });
    await expect(field).toBeVisible();
    await shot(page, 'id5-setting-light-375');

    const api = page.request;
    const settings = (await (await api.get('/api/v1/security/session-settings')).json()) as {
      idleTimeoutMinutes: number;
      minIdleTimeoutMinutes: number;
    };
    expect(settings.idleTimeoutMinutes).toBe(1);
    expect(settings.minIdleTimeoutMinutes, 'стенд знижує мінімум до 1').toBe(1);

    const me = (await (await api.get('/api/v1/me')).json()) as { idleTimeoutMinutes: number };
    expect(me.idleTimeoutMinutes).toBe(1);

    const today = new Date().toISOString().slice(0, 10);
    const audit = await api.get(
      `/api/v1/audit/security?from=${today}T00:00:00Z&to=${today}T23:59:59Z&eventType=SessionSettingsUpdated&limit=20`,
    );
    expect(audit.ok(), `журнал: ${String(audit.status())}`).toBe(true);
    expect(await audit.text()).toContain('idleTimeoutMinutes');
  });

  test('Idle sign-out: id1 a polling-only session is rejected by the server at N min', async () => {
    // Сеанс `poller` відкрито до зміни (30 хв на вході) і він лише ОПИТУЄ: GET продовження не дає.
    let lastOk = 0;
    let rejectedAt = 0;
    let body = '';
    while (Date.now() - pollerLoggedInAt < 120_000) {
      const response = await poller.get('/api/v1/jobs/summary');
      if (response.status() === 200) lastOk = Date.now();
      if (response.status() === 401) {
        rejectedAt = Date.now();
        body = await response.text();
        break;
      }
      await new Promise((resolve) => setTimeout(resolve, 5_000));
    }

    expect(lastOk, 'опитування до межі відповідали 200').toBeGreaterThan(0);
    expect(rejectedAt, 'сервер так і не відкинув сеанс, що лише опитує').toBeGreaterThan(0);
    expect(rejectedAt - pollerLoggedInAt, 'не раніше за межу').toBeGreaterThanOrEqual(60_000);
    expect(body).toContain('err.ECR-AUTH-0401.idleTimeout');
    expect(body).toContain('"idleMinutes":1');
  });

  test('Idle sign-out: id1+id3 an idle tab warns, then lands on sign-in with the reason', async ({ page }) => {
    await page.setViewportSize({ width: 1335, height: 860 });
    await signIn(page);
    const startedAt = Date.now();

    const warning = page.getByRole('alertdialog');
    await expect(warning, 'попередження за 30 с до межі').toBeVisible({ timeout: 45_000 });
    expect(Date.now() - startedAt, 'попередження не раніше за 30 с').toBeGreaterThanOrEqual(25_000);
    await expect(page.getByRole('button', { name: /Stay signed in/i })).toBeFocused();
    await expect(page.getByRole('timer')).toHaveText(/0:\d\d/);
    await shot(page, 'id3-warning-light-1335');

    await page.waitForURL((url) => url.pathname.startsWith('/login'), { timeout: 60_000 });
    expect(new URL(page.url()).searchParams.get('reason')).toBe('idle');
    await expect(page.locator('[data-login-reason="idle"]')).toContainText(IdleRe, { timeout: 30_000 });
    await shot(page, 'id1-signed-out-light-1335');

    // Сервер теж завершив сеанс: прямий запит — 401.
    expect((await page.request.get('/api/v1/me')).status()).toBe(401);
  });

  test('Idle sign-out: id2 a click or keypress resets the clock', async ({ page }) => {
    await page.setViewportSize({ width: 1335, height: 860 });
    await signIn(page);

    await idle(page, 20_000);
    await page.mouse.click(5, 300);
    await idle(page, 20_000);
    await page.keyboard.press('Shift');

    // ~62 с від входу: без дій — уже вихід (межа 60 с); з діями (остання о 40-й) — навіть без попередження (о 70-й).
    await idle(page, 22_000);
    await expect(page.getByRole('alertdialog')).toHaveCount(0);
    expect(new URL(page.url()).pathname.startsWith('/login'), 'вийшло, попри дії').toBe(false);
    expect((await page.request.get('/api/v1/me')).status(), 'сервер теж бачить дії').toBe(200);
  });

  test('Idle sign-out: id3 «Stay signed in» resets the clock (dark)', async ({ page }) => {
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.setViewportSize({ width: 1335, height: 860 });
    await signIn(page);

    const warning = page.getByRole('alertdialog');
    await expect(warning).toBeVisible({ timeout: 45_000 });
    await shot(page, 'id3-warning-dark-1335');
    await page.getByRole('button', { name: /Stay signed in/i }).click();
    await expect(warning).toHaveCount(0);

    // 35 с після «Stay»: ~65 с від входу — без «Stay» тут був би вихід (межа 60 с); з ним — вихід лише о ~90-й.
    await idle(page, 35_000);
    expect(new URL(page.url()).pathname.startsWith('/login'), 'вихід, попри «Stay signed in»').toBe(false);
    expect((await page.request.get('/api/v1/me')).status(), 'сервер отримав сигнал «Stay»').toBe(200);
  });

  test('Idle sign-out: id4 a second tab follows the first — alive and signed out together', async ({ page }) => {
    await page.setViewportSize({ width: 1335, height: 860 });
    await signIn(page);
    const second = await page.context().newPage();
    await second.goto('/');
    await expect(second.getByRole('banner')).toBeVisible({ timeout: 30_000 });

    // 80 с дій ЛИШЕ в першій вкладці — друга не попереджає й не виходить.
    for (let i = 0; i < 4; i += 1) {
      await idle(page, 20_000);
      await page.mouse.click(5, 300);
      await expect(second.getByRole('alertdialog')).toHaveCount(0);
    }
    expect(new URL(second.url()).pathname.startsWith('/login'), 'друга вкладка вийшла, попри дії в першій').toBe(false);

    // Тиша в обох — виходять разом, обидві з причиною.
    await Promise.all([
      page.waitForURL((url) => url.pathname.startsWith('/login'), { timeout: 90_000 }),
      second.waitForURL((url) => url.pathname.startsWith('/login'), { timeout: 90_000 }),
    ]);
    expect(new URL(page.url()).searchParams.get('reason')).toBe('idle');
    expect(new URL(second.url()).searchParams.get('reason')).toBe('idle');
    await second.close();
  });

  test('Idle sign-out: id6 the audit row shows the reason «idle»', async ({ playwright, page }) => {
    const api = await newApi(playwright.request, baseURL);
    const today = new Date().toISOString().slice(0, 10);
    const response = await api.get(
      `/api/v1/audit/security?from=${today}T00:00:00Z&to=${today}T23:59:59Z&eventType=SignedOut&limit=50`,
    );
    expect(response.ok()).toBe(true);
    const text = await response.text();
    expect(text).toContain('\\"reason\\":\\"idle\\"');
    expect(text).toContain('\\"by\\":\\"server\\"');
    expect(text).toContain('\\"by\\":\\"client\\"');

    await page.setViewportSize({ width: 1335, height: 860 });
    await signIn(page);
    await page.goto(`/admin/audit?view=security&eventType=SignedOut&from=${today}&to=${today}`);
    await expect(page.getByText('SignedOut').first()).toBeVisible({ timeout: 30_000 });
    await shot(page, 'id6-audit-row-light-1335');
  });
});
