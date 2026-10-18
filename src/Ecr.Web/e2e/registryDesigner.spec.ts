import { expect, test, type Page } from '@playwright/test';

/**
 * `REG-UI`: екран записів довідника і конструктор — на живому стенді.
 *
 * Що доводиться тут, а не у vitest: рядок дій сторінки записів («Ще», «Назад») над живим
 * переліком, вкладки конструктора з підказками, нове поле `Lookup` (ціль обирається назвою) і поле
 * `Decimal` (одиниця обирається зі стендового переліку), збереження чернетки з перечитуванням, а
 * також обидва екрани в світлій і темній темі зі знімком (вкладається у звіт Playwright).
 *
 * ⚠ Довідники заводяться через API тим самим входом, що й людина (`page.request` ділить куки з
 * вкладкою): сід довідників не заводить — це дані. Видалення довідника API не має, тож коди
 * унікальні за часом, а залишок — порожні довідники `E2E_DSN_*` / `E2E_LKP_*`.
 *
 * Запуск (стенд — `tools/e2e-stand.ps1`): `npx playwright test -g "Конструктор довідника"`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

async function signIn(page: Page, scheme: 'light' | 'dark'): Promise<void> {
  await page.goto('/login');
  // ⛔ Тема — у сховище ДО входу: меню користувача знову було б шляхом через інтерфейс.
  await page.evaluate((colour: string) => {
    localStorage.setItem('mantine-color-scheme-value', colour);
  }, scheme);
  await page.reload();
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({
    timeout: 30_000,
  });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

async function createRegistry(page: Page, code: string, name: string): Promise<void> {
  const created = await page.request.post('/api/v1/registries', {
    data: { code, nameL10n: { en: name }, isTemporal: false },
  });
  expect(created.status(), `створення довідника ${code}: ${await created.text()}`).toBe(201);
}

test.describe('Конструктор довідника: записи, вкладки, нові поля', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  for (const scheme of ['light', 'dark'] as const) {
    test(`${scheme}: рядок дій записів, вкладки, поля Lookup і Decimal, чернетка`, async ({ page }, testInfo) => {
      test.setTimeout(180_000);
      await page.setViewportSize({ width: 1440, height: 900 });
      await signIn(page, scheme);

      const stamp = `${scheme.toUpperCase()}_${Date.now()}`;
      const owner = `E2E_DSN_${stamp}`;
      const target = `E2E_LKP_${stamp}`;
      const targetName = `E2E lookup target ${stamp}`;
      await createRegistry(page, target, targetName);
      await createRegistry(page, owner, `E2E designer ${stamp}`);

      // --- Екран записів: рядок дій.
      await page.goto(`/admin/registries?code=${owner}`);
      await expect(page.getByRole('link', { name: /All registries/ })).toBeVisible({ timeout: 30_000 });
      await expect(page.getByRole('button', { name: 'New entry' })).toBeVisible();

      const more = page.getByRole('button', { name: 'More' });
      await more.focus();
      await page.keyboard.press('Enter');
      await expect(page.getByRole('menuitem', { name: 'Registry designer' })).toBeVisible();
      await testInfo.attach(`records-${scheme}`, {
        body: await page.screenshot({ fullPage: true }),
        contentType: 'image/png',
      });

      // --- Перехід у конструктор з меню клавіатурою.
      await page.getByRole('menuitem', { name: 'Registry designer' }).focus();
      await page.keyboard.press('Enter');
      await expect(page).toHaveURL(new RegExp(`/admin/registries/${owner}/definition`));
      await expect(page.getByRole('link', { name: /Back to registries/ })).toBeVisible({ timeout: 30_000 });

      // --- Вкладки з підказками. Довідник локальний і без мапінгу — вкладки «Mapping» немає.
      await expect(page.getByRole('tab', { name: 'Fields' })).toBeVisible();
      await expect(page.getByRole('tab', { name: 'Mapping' })).toHaveCount(0);

      await page.getByRole('tab', { name: 'Relations' }).click();
      await expect(page.getByText(/Relations are derived from fields/)).toBeVisible();
      await page.getByRole('tab', { name: 'History' }).click();
      await expect(page.getByText(/Who changed the description of this registry/)).toBeVisible();
      await page.getByRole('tab', { name: 'Where used' }).click();
      await expect(page.getByText(/Templates and columns that depend on this registry/)).toBeVisible();
      await page.getByRole('tab', { name: 'Fields' }).click();

      // --- Поле Lookup: ціль обирається назвою довідника.
      const addField = page.getByRole('button', { name: 'Add field' });
      await addField.focus();
      await page.keyboard.press('Enter');
      const forms = page.locator('[data-registry-field-form]');
      await expect(forms).toHaveCount(1);
      const lookupForm = forms.nth(0);
      await lookupForm.getByRole('textbox', { name: /^Code/ }).fill('PARENT');
      await lookupForm.getByRole('textbox', { name: /^Name/ }).fill('Parent registry');
      await lookupForm.getByRole('combobox', { name: /^Type/ }).selectOption('Lookup');
      await lookupForm
        .getByRole('combobox', { name: /^Looks up/ })
        .selectOption({ label: `${targetName} (${target})` });

      // --- Поле Decimal: одиниця обирається з наявного переліку стенда (перша не порожня).
      await addField.click();
      await expect(forms).toHaveCount(2);
      const decimalForm = forms.nth(1);
      await decimalForm.getByRole('textbox', { name: /^Code/ }).fill('VOLUME');
      await decimalForm.getByRole('textbox', { name: /^Name/ }).fill('Volume');
      await decimalForm.getByRole('combobox', { name: /^Type/ }).selectOption('Decimal');
      const unitSelect = decimalForm.getByRole('combobox', { name: /^Unit/ });
      await expect(unitSelect).toBeVisible();
      await expect(async () => {
        expect(await unitSelect.locator('option').count()).toBeGreaterThan(1);
      }).toPass({ timeout: 15_000 });
      await unitSelect.selectOption({ index: 1 });
      const pickedUnit = await unitSelect.inputValue();
      expect(pickedUnit, 'одиницю не обрано').not.toBe('');

      // --- Чернетка: причина обов'язкова, далі збереження й перечитування.
      await page.getByRole('textbox', { name: /^Reason/ }).fill('e2e: registry designer');
      const save = page.getByRole('button', { name: 'Save draft' });
      await expect(save).toBeEnabled();
      await testInfo.attach(`designer-${scheme}`, {
        body: await page.screenshot({ fullPage: true }),
        contentType: 'image/png',
      });
      await save.click();
      await expect(page.getByText('The draft has been saved.')).toBeVisible({ timeout: 30_000 });

      // ⚠ Нові поля до публікації живуть чернеткою: після перезавантаження форма засівається нею,
      // і Lookup показує ціль НАЗВОЮ, а Decimal — обрану одиницю.
      await page.reload();
      await expect(forms).toHaveCount(2, { timeout: 30_000 });
      await expect(forms.nth(0).getByRole('textbox', { name: /^Code/ })).toHaveValue('PARENT');
      await expect(forms.nth(0).getByRole('combobox', { name: /^Looks up/ })).toHaveValue(/\d+/);
      await expect(
        forms.nth(0).getByRole('combobox', { name: /^Looks up/ }).locator('option:checked'),
      ).toHaveText(`${targetName} (${target})`);
      await expect(forms.nth(1).getByRole('combobox', { name: /^Unit/ })).toHaveValue(pickedUnit);
    });
  }
});
