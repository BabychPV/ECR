import { expect, test, type Page } from '@playwright/test';

/**
 * `UI-39`: сторінка Health за макетом (`docs/design/hybrid/screens-ops.js`,
 * `/admin/health`) — на живому стенді.
 *
 * Що доводиться тут, а не у vitest: справжні перевірки сервера (усі з
 * `Program.cs`, а не фікстура) стають секціями з людськими назвами, «Check now»
 * справді перепитує `/health/ready`, «Copy diagnostics» кладе в буфер звіт без
 * секретів (немає рядка з'єднання, пароля, відбитків сертифікатів).
 *
 * Запуск (стенд — `tools/e2e-stand.ps1`): `npx playwright test -g "Health"`.
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

test.describe('Health: банер, секції, Check now, Copy diagnostics (UI-39)', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  test('секції з назвами, «Check now» перепитує стан, діагностика без секретів', async ({ page, context }) => {
    test.setTimeout(120_000);
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    await signIn(page);
    await page.goto('/admin/health');

    // Секція бази — завжди (подробиці `/health/db`), решта — з `/health/ready`.
    const db = page.locator('[data-health-section="db"]');
    await expect(db).toBeVisible({ timeout: 30_000 });
    await expect(page.locator('[data-health-section="jobs"]')).toBeVisible();
    await expect(page.locator('[data-health-section="sources"]')).toBeVisible();

    // ⛔ Заголовки — підписи каталогу, а не ідентифікатори перевірок (`U-14`).
    for (const raw of ['db', 'jobs', 'sources', 'worker', 'tzdata', 'transport']) {
      await expect(page.getByRole('heading', { name: raw, exact: true })).toHaveCount(0);
    }

    // Банер є рівно тоді, коли зведений стан не Healthy.
    const overall = await page.locator('main [data-status-state]').first().getAttribute('data-status-state');
    const banner = page.getByTestId('health-banner');
    if (overall === 'Healthy') await expect(banner).toHaveCount(0);
    else await expect(banner).toBeVisible();

    const recheck = page.waitForResponse((r) => r.url().includes('/health/ready'));
    await page.getByRole('button', { name: /Check now|Проверить сейчас|Қазір тексеру/ }).click();
    expect((await recheck).status()).toBeLessThan(600);

    await page.getByRole('button', { name: /Copy diagnostics|Копировать диагностику|Диагностиканы көшіру/ }).click();
    const text = await page.evaluate(() => navigator.clipboard.readText());

    expect(text).toMatch(/^ECR diagnostics · /);
    expect(text).toContain('Overall: ');
    expect(text).toMatch(/^db: /m);
    expect(text).not.toMatch(/Password|Server=|Data Source=|unreadableKeyCertificates/i);
  });
});
