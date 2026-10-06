import { expect, test, type Page } from '@playwright/test';

/**
 * Інспектор документа (`UI-25`) на живому стенді: закритий за замовчуванням,
 * кнопка зауважень відкриває його, `Esc` закриває й повертає фокус, History
 * показує вкладку для вибраної комірки. Запуск «Аудитом»:
 * `tools/e2e-stand.ps1 … -Grep "інспектор"`.
 */

const Operator = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

test.describe('інспектор документа', () => {
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );

  test('інспектор: закритий за замовчуванням, відкривається кнопкою, Esc повертає фокус', async ({ page }) => {
    test.slow();
    await signIn(page);
    await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
    await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });

    // L2: на відкритті інспектора немає.
    const triggers = page.locator('[data-inspector-triggers]');
    await expect(triggers).toBeVisible({ timeout: 30_000 });
    await expect(page.getByRole('complementary')).toHaveCount(0);

    const issues = triggers.getByRole('button').first();
    await issues.focus();
    await issues.press('Enter');

    const aside = page.getByRole('complementary');
    await expect(aside).toBeVisible();
    await expect(page).toHaveURL(/[?&]panel=issues/);
    await expect(aside.getByRole('tab', { selected: true })).toBeFocused();

    await page.keyboard.press('Escape');
    await expect(page.getByRole('complementary')).toHaveCount(0);
    await expect(issues).toBeFocused();
    await expect(page).not.toHaveURL(/[?&]panel=/);
  });

  test('інспектор: History і Info показують вибрану комірку', async ({ page }) => {
    test.slow();
    await signIn(page);
    await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);

    const grid = page.locator('revo-grid').first();
    await expect(grid).toBeVisible({ timeout: 60_000 });
    await grid.locator('revogr-data [data-rgcol]').first().click();
    await expect(page.locator('[data-formula-bar="cell"]').first()).toBeVisible();

    await page.locator('[data-inspector-triggers]').getByRole('button').nth(1).click();
    const aside = page.getByRole('complementary');
    await expect(aside).toBeVisible();
    await expect(page).toHaveURL(/[?&]panel=history/);
    await expect(aside.locator('[data-inspector-address]')).toBeVisible();

    await aside.getByRole('tab').nth(2).click();
    await expect(aside.locator('[data-inspector-info]')).toBeVisible();
  });
});

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Operator.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Operator.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}
