import { expect, test, type Page } from '@playwright/test';

/**
 * PM-08a: перелік типів пермітів і майстер «New permit type» — на живому стенді.
 *
 * Що доводиться тут, а не у vitest: справжній маршрут `/admin/permits/types` з правом
 * `Registry.EditDefinition`, справжній `POST /api/v1/permit-types` (код, категорія, 201), перехід у редактор
 * і поява нового типу в переліку після перезавантаження.
 *
 * Запуск (стенд — `tools/e2e-stand.ps1`; на CI — workflow `e2e-stand`):
 * `npx playwright test -g "Типи пермітів"`.
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

test.describe('Типи пермітів: перелік і майстер', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  test('новий тип створюється майстром і з’являється в переліку', async ({ page }) => {
    test.setTimeout(120_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);

    // Назва унікальна на прогін: код береться з неї, а повторне заведення дало б 409 ECR-PRM-4091.
    const stamp = String(Date.now());
    const name = `E2E noise permit ${stamp}`;
    const code = `E2ENoisePermit${stamp}`;

    await page.goto('/admin/permits/types');
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible({ timeout: 30_000 });
    await expect(page.getByRole('navigation', { name: /Permits sections|Разделы|Бөлімдер/i })).toBeVisible();

    await page.getByRole('button', { name: /^New type$|^Новый тип$|^Жаңа түр$/ }).first().click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible({ timeout: 10_000 });

    // Порожня назва не пускає далі.
    await dialog.getByTestId('wizard-next').click();
    await expect(dialog.getByTestId('wizard-step-error')).toBeVisible();

    await dialog.getByRole('textbox', { name: /^Name|^Название|^Атауы/ }).fill(name);
    await dialog.getByTestId('wizard-next').click();
    await expect(dialog.getByTestId('wizard-summary')).toContainText(name);

    await dialog.getByTestId('wizard-apply').click();
    await expect(page).toHaveURL(new RegExp(`/admin/permits/types/${code}$`), { timeout: 30_000 });

    // Тип видно в переліку після перезавантаження.
    await page.goto('/admin/permits/types');
    const row = page.locator('[data-list-table] table tbody tr', { hasText: name });
    await expect(row).toBeVisible({ timeout: 30_000 });
    await row.getByRole('link', { name }).click();
    await expect(page).toHaveURL(new RegExp(`/admin/permits/types/${code}$`));
  });
});
