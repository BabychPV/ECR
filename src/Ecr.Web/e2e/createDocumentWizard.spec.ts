import { expect, test, type Page } from '@playwright/test';

/**
 * UI-31: «New document» — майстер (`KIT.md` §6.9, макет `screens-work.js` `openCreate`):
 * Project → Period → Sheets → Review. Прогін у браузері на стенді: справжній лінивий чанк,
 * справжні `document-template`/`periods`, повернення фокуса на кнопку-відкривач.
 *
 * ⚠ Документ НЕ створюється: прогін доходить до кроку Review і закривається — стенд
 * лишається тим самим для решти наборів.
 */
const Operator = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

test.describe('Майстер створення документа', () => {
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );

  test('модальний майстер «New document»: кроки до Review, Esc повертає фокус на кнопку', async ({ page }) => {
    await signIn(page, Operator.user, Operator.password);
    await page.goto('/');

    const opener = page.getByRole('button', { name: /New document|Новый документ|Жаңа құжат/i }).first();
    await expect(opener, 'немає кнопки створення документа').toBeVisible({ timeout: 30_000 });
    await opener.click();

    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible({ timeout: 10_000 });
    await expect(dialog.getByTestId('wizard-steps')).toBeVisible();

    const next = dialog.getByTestId('wizard-next');
    await expect(next, 'Next доступний до вибору проєкту').toBeDisabled();

    // Перший активний проєкт стенда.
    await dialog.getByRole('textbox').first().click();
    // ⚠ Не `getByRole('option').first()`: на сторінці є нативний `<select>` фільтра
    // стану, і перший `option` — його «All states», а не проєкт.
    await page.getByRole('listbox', { name: /Project|Проект/i }).getByRole('option').first().click();
    await expect(next, 'склад нового документа не приїхав').toBeEnabled({ timeout: 15_000 });

    await next.click(); // → Period
    await next.click(); // → Sheets
    const boxes = dialog.getByTestId('wizard-step-body').getByRole('checkbox');
    await expect(boxes.first(), 'у кроці Sheets немає жодного аркуша').toBeVisible();
    for (const box of await boxes.all()) await expect(box).toBeChecked();

    await next.click(); // → Review
    await expect(dialog.getByTestId('wizard-summary')).toBeVisible();
    await expect(dialog.getByTestId('wizard-apply')).toBeVisible();

    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden({ timeout: 10_000 });
    await expect(opener, 'фокус не повернувся на кнопку-відкривач').toBeFocused();
  });
});

async function signIn(page: Page, user: string, password: string): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({
    timeout: 30_000,
  });

  await page.getByLabel(/User name|Ім'я/i).fill(user);
  // ⚠ Роль, а не підпис: `getByLabel(/Password|Пароль/i)` резолвиться і в кнопку-тумблер
  // видимості пароля (`LoginPage.tsx`, Q-260).
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}
