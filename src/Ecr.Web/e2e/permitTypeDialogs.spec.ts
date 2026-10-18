import { expect, test, type Page } from '@playwright/test';
import { CreatedPermitTypes } from './permitTypeCleanup';

/**
 * Permits PM-08c: діалоги конструктора типу дозволу на живому стенді — майстер «Add a table»,
 * «Add a column», «Add 12 month columns», «Add a check» і публікація з причиною.
 *
 * Що доводиться тут, а не у vitest: справжній модальний фокус і Escape у вкладеному діалозі,
 * `?dialog=<id>` в адресі (діалог переживає перезавантаження), результат діалогу в дереві
 * конструктора й версія типу на сервері після публікації.
 *
 * ⚠ Сценарій іде від порожнього типу, який сам заводить через API (`page.request` ділить куки з
 * вкладкою): сід типів дозволів не заводить. Опублікований тип видалити не можна (`ECR-PRM-4094`),
 * тому код кожного прогону унікальний.
 *
 * Запуск (стенд — `tools/e2e-stand.ps1`): `npx playwright test -g "Діалоги конструктора типу дозволу"`.
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

test.describe('Діалоги конструктора типу дозволу', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  // Тест у кінці ПУБЛІКУЄ тип, а опублікований сервер не видаляє (`ECR-PRM-4094`): прибирання знімає лише
  // той тип, що не дійшов до публікації (тест упав раніше); готовий `E2E_Dialogs_*` лишається на стенді
  // для одноразової бази (`e2e-stand` її розгортає наново).
  const tracked = new CreatedPermitTypes();
  test.afterEach(async ({ page }) => {
    await tracked.cleanup(page);
  });

  test('Add table → Add column → Add check → Publish', async ({ page }) => {
    test.setTimeout(180_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);

    const code = tracked.track(`E2E_Dialogs_${Date.now()}`);
    const created = await page.request.post('/api/v1/permit-types', {
      data: { code, nameL10n: { en: 'E2E dialogs' }, category: 'Water' },
    });
    expect(created.status(), `заведення типу: ${await created.text()}`).toBe(201);

    // 1. Майстер «Add a table»: адреса тримає `?dialog=`, Escape без введеного закриває одразу.
    await page.goto(`/admin/permits/types/${code}?dialog=add-table`);
    const wizard = page.getByRole('dialog');
    await expect(wizard).toBeVisible({ timeout: 30_000 });
    await page.keyboard.press('Escape');
    await expect(wizard).toBeHidden();
    await expect(page).not.toHaveURL(/[?&]dialog=/);

    await page.goto(`/admin/permits/types/${code}?dialog=add-table`);
    await wizard.getByLabel(/^Name \(English\)/).fill('Monitoring points');
    await wizard.getByRole('button', { name: 'Next' }).click();
    // Крок «Columns»: єдиний стовпець «Name» уже ключовий; додаємо числовий.
    await wizard.getByRole('button', { name: 'Add column' }).click();
    await wizard.getByLabel('Column 2').fill('Annual volume');
    await wizard.getByRole('button', { name: 'Next' }).click();
    await expect(wizard.getByText('PRM_' + code + '_MonitoringPoints')).toBeVisible();
    await wizard.getByRole('button', { name: 'Add table' }).click();
    await expect(wizard).toBeHidden();
    await expect(page.getByText('Monitoring points').first()).toBeVisible();

    // 2. «Add a column» у вибраній таблиці.
    await page.getByRole('button', { name: 'Add column' }).first().click();
    const column = page.getByRole('dialog');
    await column.getByLabel('Name (English)').fill('Note');
    await column.getByRole('button', { name: 'Add column' }).click();
    await expect(column).toBeHidden();
    await expect(page.getByText('Note').first()).toBeVisible();

    // 3. «Add a check»: без виразу діалог лишається, фокус на полі.
    await page.goto(`/admin/permits/types/${code}?dialog=add-rule`);
    const rule = page.getByRole('dialog');
    await rule.getByLabel(/^What it checks/).fill('Volume is not negative');
    await rule.getByRole('button', { name: 'Add check' }).click();
    await expect(rule.getByLabel(/^Rule/)).toBeFocused();
    await rule.getByLabel(/^Rule/).fill('ROW.Annual_volume >= 0');
    await rule.getByRole('button', { name: 'Add check' }).click();
    await expect(rule).toBeHidden();

    // 4. Публікація: кнопка вимкнена, доки причина коротша за 10 символів.
    await page.goto(`/admin/permits/types/${code}?dialog=publish-type`);
    const publish = page.getByRole('dialog');
    await expect(publish.getByRole('heading', { name: /Publish .* v1\?/ })).toBeVisible();
    await publish.getByLabel('What changed').fill('short');
    await expect(publish.getByRole('button', { name: 'Publish' })).toBeDisabled();
    await publish.getByLabel('What changed').fill('First version of the dialogs type');
    await publish.getByRole('button', { name: 'Publish' }).click();
    await expect(publish).toBeHidden({ timeout: 30_000 });

    const type = (await (await page.request.get(`/api/v1/permit-types/${code}`)).json()) as {
      status: string;
      tables: { code: string; columns: { code: string }[] }[];
    };
    expect(type.status).toBe('Published');
    expect(type.tables.map((t) => t.code)).toContain('MonitoringPoints');
  });
});
