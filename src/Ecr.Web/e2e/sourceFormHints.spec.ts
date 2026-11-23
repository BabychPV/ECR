import { expect, test, type Locator, type Page } from '@playwright/test';

/**
 * Діалог «Нове з'єднання» (`/admin/sources`) на живому стенді: підказки полів видно, поля перемикаються з
 * транспортом, а поля, яких транспорт не читає, приховані (Sql — запасна адреса, PI SQL Client — каталог).
 *
 * Що доводиться тут, а не у vitest: справжній каталог текстів (сід `COLL-source-form-hints.sql`), а не
 * `⟦ключ⟧`, і справжній Select Mantine у браузері. Нічого не зберігається — діалог закривається «Скасувати».
 * Перевірки мовонезалежні: поля — за `data-field`, підказки — за технічними фрагментами, однаковими в en/ru/kz.
 *
 * Запуск (стенд — `tools/e2e-stand.ps1`): `npx playwright test sourceFormHints`.
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

/** Текст підказки поля (`aria-describedby` — опис і, за наявності, помилка). */
async function hintOf(input: Locator): Promise<string> {
  const ids = ((await input.getAttribute('aria-describedby')) ?? '').split(/\s+/).filter(Boolean);
  const parts = await Promise.all(ids.map((id) => input.page().locator(`[id="${id}"]`).innerText()));
  return parts.join(' ');
}

async function pickTransport(dialog: Locator, transport: 'PiWebApi' | 'PiSqlClient' | 'Sql'): Promise<void> {
  await dialog.getByLabel(/^(Transport|Транспорт)/).click();
  await dialog.page().getByRole('option', { name: transport, exact: true }).click();
}

test.describe("Діалог «Нове з'єднання»: підказки й поля за транспортом", () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  test('підказки видно; поля перемикаються з транспортом; Sql ховає запасну адресу', async ({ page }) => {
    test.setTimeout(120_000);
    await signIn(page);
    await page.goto('/admin/sources');

    await page.locator('[data-new-connection]').click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.locator('[data-data-source-form]')).toBeVisible({ timeout: 30_000 });

    const endpoint = dialog.locator('input[data-field="endpoint"]');
    const secondary = dialog.locator('input[data-field="secondaryEndpoint"]');
    const catalog = dialog.locator('input[data-field="catalog"]');

    // Типово — PI SQL Client: рядок ODBC у підказці й прикладі; запасна адреса є, каталогу немає.
    await expect(endpoint).toBeVisible();
    expect(await hintOf(endpoint)).toContain('Driver={PI SQL Client}');
    await expect(endpoint).toHaveAttribute('placeholder', /Driver=\{PI SQL Client\}/);
    await expect(secondary).toBeVisible();
    expect((await hintOf(secondary)).length).toBeGreaterThan(20);
    await expect(catalog).toHaveCount(0);

    // Підказка стелі паралельності — справжній текст каталогу (межі 1..32), не ключ.
    const parallel = dialog.getByLabel(/Max parallel|Макс\.|параллель/i);
    const parallelHint = await hintOf(parallel);
    expect(parallelHint).toContain('32');
    expect(parallelHint).not.toContain('sources.');

    await pickTransport(dialog, 'PiWebApi');
    expect(await hintOf(endpoint)).toMatch(/https?/);
    await expect(endpoint).toHaveAttribute('placeholder', 'https://pi.example.local/piwebapi');
    await expect(secondary).toBeVisible();
    await expect(catalog).toBeVisible();
    await expect(catalog).toHaveAttribute('placeholder', 'ECR');

    await pickTransport(dialog, 'Sql');
    expect(await hintOf(endpoint)).toContain('Server=');
    await expect(endpoint).toHaveAttribute('placeholder', /^Server=sql01\.example\.local;/);
    // ⛔ Sql не має запасної адреси: поля немає зовсім (сервер такого значення не збереже).
    await expect(secondary).toHaveCount(0);
    await expect(catalog).toBeVisible();

    // Нічого не зберігаємо: діалог закривається Escape.
    await page.keyboard.press('Escape');
    await expect(dialog.locator('[data-data-source-form]')).toHaveCount(0);
  });
});
