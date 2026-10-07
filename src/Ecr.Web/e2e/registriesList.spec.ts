import { expect, test, type Page } from '@playwright/test';

/**
 * `UI-35`: перелік довідників зі шторкою — на живому стенді.
 *
 * Що доводиться тут, а не у vitest: шторка справжньої ширини поруч із живою
 * таблицею (`DetailDrawer` без оверлея від 1200 px), лінивий чанк шторки
 * вантажиться й відкривається за адресою, фокус після `Escape` повертається
 * на назву в рядку, «Open data» веде в табличний редактор.
 *
 * Запуск (стенд — `tools/e2e-stand.ps1`): `npx playwright test -g "Перелік довідників"`.
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

test.describe('Перелік довідників: шторка і «Open data»', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  test('шторка за ?panel=, фокус повертається на рядок, «Open data» веде в /entries', async ({ page }) => {
    test.setTimeout(120_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);

    // ⚠ Свіжий стенд довідників не має (сід їх не заводить — це дані), а перелік
    // без рядків не малює таблицю. Тому спека сама заводить порожній довідник
    // тим самим входом, що й людина (`page.request` ділить куки з вкладкою).
    // Поле не потрібне: тут перевіряється шторка й «Open data», а не записи.
    const existing = await page.request.get('/api/v1/registries');
    const list = existing.ok() ? ((await existing.json()) as unknown[]) : [];
    if (list.length === 0) {
      const created = await page.request.post('/api/v1/registries', {
        data: { code: `E2E_REG_${Date.now()}`, nameL10n: { en: 'E2E registry' }, isTemporal: false },
      });
      expect(created.status(), `створення довідника: ${await created.text()}`).toBe(201);
    }

    await page.goto('/admin/registries');

    const table = page.locator('[data-list-table] table');
    await expect(table).toBeVisible({ timeout: 30_000 });

    // ⛔ Вхід у довідник — перелік, а не `Select` у шапці.
    await expect(page.getByRole('textbox', { name: /^Registries$|^Справочники$/ })).toHaveCount(0);

    const opener = table.locator('[data-registry-open]').first();
    const code = await opener.getAttribute('data-registry-open');
    expect(code, 'у сіді немає жодного довідника').not.toBeNull();

    await opener.focus();
    await page.keyboard.press('Enter');

    await expect(page).toHaveURL(new RegExp(`[?&]panel=${code ?? ''}(&|$)`));
    await expect(page.getByRole('dialog')).toBeVisible();

    // Сторінка позаду жива: таблицю видно поруч зі шторкою (широкий екран).
    // ⚠ `data-panel` стоїть на корені Drawer нульового розміру — лише атрибут, не видимість.
    await expect(page.locator(`[data-panel="${code ?? ''}"]`)).toHaveAttribute('data-wide', 'true');
    await expect(table).toBeVisible();

    await page.keyboard.press('Escape');
    await expect(page).not.toHaveURL(/[?&]panel=/);
    await expect(opener).toBeFocused();

    await opener.click();
    await page.getByRole('link', { name: /Open data|Открыть данные|Деректерді ашу/ }).click();
    await expect(page).toHaveURL(new RegExp(`/admin/registries/${code ?? ''}/entries`));
  });
});
