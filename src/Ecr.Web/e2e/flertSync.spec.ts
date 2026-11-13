import { expect, test, type Page } from '@playwright/test';

/**
 * D-476 F3: «Синхронізувати зараз» і стан синку складу газу FLERT на сторінці довідника-дзеркала — на живому стенді.
 *
 * Що доводиться тут, а не у vitest: справжній ланцюжок кнопка → `POST /api/v1/integration/flert-sync` → задача в черзі
 * стенда → рядок `itg.MaintenanceRun` → `GET …/status` → смуга оновилася сама (опитування після постановки).
 *
 * ⚠ FLERT на стенді немає, і синк вимкнено (`Flert:Sync:Enabled=false`, дефолт): прогін лише пише «вимкнено» —
 * цього досить, щоб довести шлях від кнопки до журналу і назад. Довідника `STREAM` свіжий стенд не має (його заводить
 * перший увімкнений синк), тож спека заводить порожній тим самим входом, що й людина.
 *
 * Запуск (стенд — `tools/e2e-stand.ps1`): `npx playwright test -g "FLERT"`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({
    timeout: 30_000,
  });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

test.describe('FLERT: «Синхронізувати зараз» і стан синку', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  test('кнопка ставить синк, смуга показує новий прогін', async ({ page }) => {
    test.setTimeout(180_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);

    const existing = await page.request.get('/api/v1/registries/STREAM/definition');
    if (existing.status() === 404) {
      const created = await page.request.post('/api/v1/registries', {
        data: { code: 'STREAM', nameL10n: { en: 'FLERT streams (e2e)' }, isTemporal: false },
      });
      expect(created.status(), `створення STREAM: ${await created.text()}`).toBe(201);
    }

    const before = await page.request.get('/api/v1/integration/flert-sync/status');
    expect(before.status(), await before.text()).toBe(200);
    const beforeRun = ((await before.json()) as { lastRun: { startedAt: string } | null }).lastRun;

    await page.goto('/admin/registries/STREAM/entries');

    const panel = page.getByTestId('flert-sync');
    await expect(panel).toBeVisible({ timeout: 30_000 });
    await expect(panel).toContainText(/FLERT/);

    await panel.getByRole('button', { name: /Sync now|Синхронизировать сейчас|Қазір синхрондау/ }).click();

    // Новий прогін (вимкнений синк — «вимкнено») з'являється в смузі без перезавантаження сторінки.
    await expect
      .poll(
        async () => {
          const reply = await page.request.get('/api/v1/integration/flert-sync/status');
          const run = ((await reply.json()) as { lastRun: { startedAt: string; finishedAt: string | null } | null }).lastRun;
          return run !== null && run.finishedAt !== null && run.startedAt !== beforeRun?.startedAt;
        },
        { timeout: 120_000 },
      )
      .toBe(true);
    // ⚠ Без урахування регістру: після P3 #841 (c) прогін при вимкненому синку — «skipped — sync is off», без окремого
    // рядка «Sync is off — nothing was read.»; про вимкнений синк каже «Daily sync is off …».
    await expect(panel).toContainText(/sync is off|синхронизация выключена|синхрондау өшірулі/i, { timeout: 30_000 });
    // P3 F13 #841 (c): прогін при вимкненому синку — «пропущено», а не «succeeded» поруч із «Sync is off».
    await expect(panel).toContainText(/skipped — sync is off|пропущена — синхронизация выключена|өткізіп жіберілді/);
    await expect(panel).not.toContainText(/succeeded|успешно|сәтті/);
  });

  test('дзеркало до першого синку: без 404 у браузері, читабельна шапка, посилання на конструктор', async ({ page }) => {
    test.setTimeout(120_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);

    // Свіжий стенд дзеркала GAS_COMPOSITION не має (його заводить лише увімкнений синк); якщо хтось завів — нема що
    // перевіряти тут.
    const existing = await page.request.get('/api/v1/registries/GAS_COMPOSITION/definition');
    test.skip(existing.status() !== 404, 'GAS_COMPOSITION уже існує на стенді — нейтрального стану немає.');

    const notFound: string[] = [];
    page.on('response', (response) => {
      if (response.status() === 404 && response.url().includes('/api/v1/registries/GAS_COMPOSITION')) {
        notFound.push(response.url());
      }
    });

    await page.goto('/admin/registries/GAS_COMPOSITION/entries');
    const pending = page.getByTestId('flert-mirror-pending');
    await expect(pending).toBeVisible({ timeout: 30_000 });

    // (d) адміністратор має `Registry.EditDefinition` — посилання на конструктор у нейтральній смузі.
    const designer = pending.getByRole('link', { name: /Registry designer|Конструктор справочника|Анықтамалық конструкторы/i });
    await expect(designer).toBeVisible();
    await expect(designer).toHaveAttribute('href', '/admin/registries');

    // (b) код у шапці — один раз і без лічильника «0».
    const header = page.locator('main').getByRole('heading', { level: 1 });
    await expect(header).toHaveText('GAS_COMPOSITION');

    // (a) сторінка не питає опису довідника, якого немає в переліку.
    await page.waitForLoadState('networkidle');
    expect(notFound, `404 на сторінці дзеркала: ${notFound.join(', ')}`).toEqual([]);
  });
});
