import { expect, test, type Page } from '@playwright/test';

/**
 * Сторінка «Склад газу» (D-NEW, людина 24.11) на живому стенді: пункт бічного меню в групі «Configure», новий потік, новий
 * кейс із першою версією, нова версія (копія попередньої → перша обрізається), правка зі Σ поза допуском лише з
 * підтвердженням, видалення версії (попередня знову чинна), видалення потоку. Знімки — світла й темна тема та 375 px.
 *
 * Права — роль `E2EAdmin` (`tools/e2e-stand.ps1`: `GasComposition.View`, `GasComposition.Edit`). Потік — з GUID у назві:
 * база стенда спільна для прогонів.
 *
 * Запуск: `npx playwright test gasComposition.spec.ts` (стенд — `tools/e2e-stand.ps1 -Grep gasComposition`).
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

/** Тема через меню користувача (вподобання зберігає сервер), і перевірка атрибута на `<html>`. */
async function setScheme(page: Page, scheme: 'light' | 'dark'): Promise<void> {
  const html = page.locator('html');
  const menu = page.getByRole('button', { name: new RegExp(Admin.user, 'i') }).first();
  await expect(menu).toBeVisible({ timeout: 30_000 });
  if ((await html.getAttribute('data-mantine-color-scheme')) !== scheme) {
    await menu.click();
    await page.getByText(scheme === 'dark' ? 'Dark' : 'Light', { exact: true }).first().click();
    await page.keyboard.press('Escape');
  }
  await expect(html).toHaveAttribute('data-mantine-color-scheme', scheme, { timeout: 15_000 });
}

/** Місяць і рік дії у редакторі версії. */
async function setEffective(page: Page, month: string, year: string): Promise<void> {
  const editor = page.getByTestId('gas-version-editor');
  await editor.getByRole('textbox', { name: 'Month' }).click();
  await page.getByRole('option', { name: month, exact: true }).click();
  await editor.getByRole('textbox', { name: 'Year' }).fill(year);
  await page.getByRole('option', { name: year, exact: true }).click();
}

/** Поле мол.% компонента в редакторі. */
const molPct = (page: Page, component: string) =>
  page.getByTestId('gas-version-editor').getByRole('textbox', { name: `${component}, mol %`, exact: true });

test.describe('Склад газу: потік, кейс, версії (D-NEW)', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  test('створити, змінити й видалити версії складу; знімки світла/темна/375', async ({ page }) => {
    test.setTimeout(240_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);
    await setScheme(page, 'light');

    // Пункт бічного меню в групі Configure.
    await page.getByRole('link', { name: 'Gas composition' }).first().click();
    await expect(page).toHaveURL(/\/admin\/gas-composition/);
    await expect(page.getByRole('heading', { level: 1, name: 'Gas composition' })).toBeVisible({ timeout: 30_000 });

    // Новий потік.
    const name = `E2E stream ${Date.now().toString(36)}`;
    await page.getByRole('button', { name: 'New stream' }).click();
    const form = page.getByTestId('gas-stream-form');
    await form.getByRole('textbox', { name: 'Name' }).fill(name);
    await form.getByRole('textbox', { name: 'Number' }).fill('E2E-1');
    const created = page.waitForResponse((r) => r.url().endsWith('/api/v1/gas-composition/streams') && r.request().method() === 'POST');
    await form.getByRole('button', { name: 'Save' }).click();
    expect((await created).status()).toBe(200);
    const detail = page.getByTestId('gas-stream-detail');
    await expect(detail).toBeVisible({ timeout: 30_000 });

    // Новий кейс з першою версією 2025-01: Σ = 100.
    await detail.getByTestId('gas-new-case').click();
    const editor = page.getByTestId('gas-version-editor');
    await editor.getByRole('textbox', { name: 'Case' }).fill('370 Winter');
    await setEffective(page, 'January', '2025');
    await molPct(page, 'Methane').fill('97');
    await molPct(page, 'Ethane').fill('2.5');
    // Компонент показано людською назвою з кодом поруч (рев'ю 25.11): «Hydrogen sulphide (H2S)», «Methyl mercaptan (CH3SH)».
    await molPct(page, 'Hydrogen sulphide (H2S)').fill('0.5');
    await expect(editor.getByText('Methyl mercaptan (CH3SH)', { exact: true })).toBeVisible();
    await expect(editor.getByTestId('gas-component-code').filter({ hasText: /^MMercaptan$/ })).toBeVisible();
    await expect(page.getByTestId('gas-sum')).toContainText('100');
    await editor.getByRole('button', { name: 'Save' }).click();
    await expect(editor).toBeHidden({ timeout: 30_000 });
    const winter = detail.locator('[data-case="370 Winter"]');
    await expect(winter.getByTestId('gas-version-row')).toHaveCount(1, { timeout: 30_000 });

    // Нова версія з 2026-03 (копія попередньої): попередня тепер діє «по лютий 2026».
    await winter.getByTestId('gas-add-version').click();
    await expect(page.getByTestId('gas-copied-from')).toBeVisible();
    await setEffective(page, 'March', '2026');
    await molPct(page, 'Methane').fill('96.5');
    await molPct(page, 'Ethane').fill('3.5');
    await molPct(page, 'Hydrogen sulphide (H2S)').fill('');
    await page.getByTestId('gas-version-editor').getByRole('button', { name: 'Save' }).click();
    await expect(winter.getByTestId('gas-version-row')).toHaveCount(2, { timeout: 30_000 });
    await expect(winter.locator('[data-from="2025-01-01"]')).toContainText('February 2026');

    await page.screenshot({ path: test.info().outputPath('gas-composition-light-1440.png') });

    // Правка новішої версії зі Σ поза допуском — «Save» недоступна до підтвердження.
    await winter.locator('[data-from="2026-03-01"]').getByRole('button', { name: 'Edit' }).click();
    await molPct(page, 'Methane').fill('90');
    const save = page.getByTestId('gas-version-editor').getByRole('button', { name: 'Save' });
    await expect(save).toBeDisabled();
    await page.getByRole('checkbox', { name: /Save with Σ = 93\.5 %/ }).check();
    await expect(save).toBeEnabled();
    await page.screenshot({ path: test.info().outputPath('gas-composition-editor-light-1440.png') });
    await save.click();
    await expect(page.getByTestId('gas-version-editor')).toBeHidden({ timeout: 30_000 });

    // Темна тема й 375 px. Меню користувача — під шторкою: закрити, перемкнути тему, відкрити потік знову.
    await page.keyboard.press('Escape');
    await expect(detail).toBeHidden({ timeout: 15_000 });
    await setScheme(page, 'dark');
    await page.getByRole('button', { name }).click();
    await expect(detail).toBeVisible({ timeout: 30_000 });
    await page.screenshot({ path: test.info().outputPath('gas-composition-dark-1440.png') });
    await page.setViewportSize({ width: 375, height: 812 });
    await page.screenshot({ path: test.info().outputPath('gas-composition-dark-375.png') });
    await page.keyboard.press('Escape');
    await expect(detail).toBeHidden({ timeout: 15_000 });
    await setScheme(page, 'light');
    await page.getByRole('button', { name }).click();
    await expect(detail).toBeVisible({ timeout: 30_000 });
    await page.screenshot({ path: test.info().outputPath('gas-composition-light-375.png') });
    await page.setViewportSize({ width: 1440, height: 900 });
    if (!(await detail.isVisible())) await page.getByRole('button', { name }).click();
    await expect(winter.getByTestId('gas-version-row')).toHaveCount(2, { timeout: 30_000 });

    // Видалення новішої версії: попередня знову чинна.
    await winter.locator('[data-from="2026-03-01"]').getByRole('button', { name: 'Delete' }).click();
    await page.getByRole('dialog', { name: /Delete the version/ }).getByRole('button', { name: 'Remove' }).click();
    await expect(winter.getByTestId('gas-version-row')).toHaveCount(1, { timeout: 30_000 });
    await expect(winter.locator('[data-from="2025-01-01"]')).toContainText('current');

    // Видалення потоку.
    await page.getByRole('button', { name: 'Delete stream…' }).click();
    const removed = page.waitForResponse((r) => r.url().includes('/api/v1/gas-composition/streams/') && r.request().method() === 'DELETE');
    await page.getByRole('dialog', { name: /Delete stream/ }).getByRole('button', { name: 'Remove' }).click();
    expect((await removed).status()).toBe(200);
    await expect(page.getByRole('button', { name })).toHaveCount(0, { timeout: 30_000 });
  });
});
