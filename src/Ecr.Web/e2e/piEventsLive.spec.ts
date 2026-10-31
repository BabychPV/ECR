import { expect, test, type Page } from '@playwright/test';

/**
 * D-465 на живому стенді: «Дані з PI → Події» для сутності-шаблону подій PI (Event Frames).
 *
 * ⚠ Зв'язки, значення атрибутів за останнім прочитанням і запис журналу видалень кладе стенд
 * (`tools/e2e/pi-events-seed.sql`, `ECR_E2E_PI_EVENTS_ENTITY`): вимкнена сутність і ПРИЗУПИНЕНИЙ мапінг на документ
 * стенда — синк не запускається, перенос версії не блокується. Сценарій лише читає (і вивантажує CSV).
 * Запуск: `tools/e2e-stand.ps1 … -Grep "події з PI"`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const EntityId = process.env['ECR_E2E_PI_EVENTS_ENTITY'] ?? '';

test.describe('події з PI', () => {
  test.skip(EntityId === '', 'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.');

  test('події з PI: список подій, причини, атрибути з порівнянням, журнал видалень, CSV', async ({ page }) => {
    test.slow();
    await signIn(page);

    // Сутність-шаблон подій відкривається одразу на «Подіях» (перемикач є, тегів — немає).
    await page.goto(`/admin/pi-data?entity=${EntityId}&range=30d`);
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible({ timeout: 30_000 });
    await expect(page.locator('[data-pi-data-view]')).toBeVisible({ timeout: 30_000 });
    const panel = page.locator('[data-pi-events]');
    await expect(panel).toBeVisible({ timeout: 30_000 });

    // Три події стенда: записана (посилання на рядок документа), незакрита, за стелею рядків — новіші першими.
    const table = page.getByRole('table', { name: 'Events from PI' });
    await expect(table.getByRole('row')).toHaveCount(4, { timeout: 30_000 });
    await expect(panel.locator('[data-pi-event-reason="Open"]')).toBeVisible();
    await expect(panel.locator('[data-pi-event-reason="RowLimit"]')).toBeVisible();
    await expect(panel.locator('[data-pi-event-document]')).toHaveCount(1);

    // Клік по записаній події — атрибути: значення PI за останнім прочитанням і порівняння з коміркою.
    await table.getByText('E2E flaring one').click();
    const detail = panel.locator('[data-pi-event-detail]');
    await expect(detail).toBeVisible();
    await expect(detail.locator('[data-pi-event-attribute="Volume"] [data-pi-event-pi-value]')).toContainText('12.5 Sm3');
    await expect(detail.locator('[data-pi-event-comparison]').first()).toBeVisible();

    // Фільтр стану: лише незакриті.
    await page.getByLabel('Link status').click();
    await page.getByRole('option', { name: 'Open', exact: true }).click();
    await page.keyboard.press('Escape');
    await expect(table.getByRole('row')).toHaveCount(2, { timeout: 30_000 });

    // Журнал видалень повною звіркою за вікно.
    await expect(page.getByText('E2E flaring removed')).toBeVisible({ timeout: 30_000 });

    // CSV — потоком із сервера; файл приходить як завантаження.
    const download = page.waitForEvent('download');
    await page.locator('[data-pi-events-export]').click();
    const file = await download;
    expect(file.suggestedFilename()).toMatch(new RegExp(`^pi-events-${EntityId}-.*\\.csv$`));

    // Перемикач «Часові ряди» — той самий екран D-463 (тег сутності без мапінгів збору).
    await page.locator('[data-pi-data-view]').getByText('Time series').click();
    await expect(page).toHaveURL(/view=series/);
    await expect(panel).toHaveCount(0);
  });
});

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}
