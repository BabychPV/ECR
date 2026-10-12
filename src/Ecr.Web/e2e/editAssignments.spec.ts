import { expect, test, type Browser, type Page } from '@playwright/test';

/**
 * EA-5g: «передача місяця» проти РЕАЛЬНОГО сервера стенда (`tools/e2e-stand.ps1`, workflow `e2e-stand`): шторка
 * «Edit access», майстер, смуга виконавця, «Заповнено», «Валідувати»/«Повернути», «My tasks».
 *
 * Автор документа - `e2e-admin`, виконавець - `e2e-operator`. Сценарій послідовний: кожен крок спирається на
 * стан попереднього (передано → заповнено → повернуто → знову заповнено → валідовано).
 *
 * ⚠ Написано без локального прогону (на машині автора немає стенда): перший живий прогін - CI. Локатори - за
 * `data-testid` і англійськими підписами каталогу (мова стенда - en).
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Executor = { user: 'e2e-operator', password: 'E2E-Oper8tor-Work-2026!', search: 'E2E operator' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';
const DocumentUrl = `/documents/${DocumentId}?periodKey=${PeriodKey}`;

async function signIn(page: Page, user: { user: string; password: string }): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(user.user);
  // Роль, а не підпис: `getByLabel(/Password/)` резолвиться ще й у кнопку-тумблер видимості пароля.
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(user.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

async function asUser(browser: Browser, user: { user: string; password: string }): Promise<Page> {
  const context = await browser.newContext();
  const page = await context.newPage();
  await signIn(page, user);

  return page;
}

async function openDrawer(page: Page): Promise<void> {
  await page.goto(DocumentUrl);
  await page.getByTestId('document-more').click({ timeout: 60_000 });
  await page.getByRole('menuitem', { name: /Edit access|Доступ к редактированию/ }).click();
  await expect(page.getByRole('dialog').first()).toBeVisible({ timeout: 15_000 });
}

async function rowActions(page: Page, itemName: RegExp): Promise<void> {
  const months = page.getByTestId('edit-assignments-months');
  await months.getByRole('button', { name: /Actions for|Действия для/ }).first().click();
  await page.getByRole('menuitem', { name: itemName }).click();
}

test.describe('Передача місяця: власник → виконавець → валідація', () => {
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );
  test.describe.configure({ mode: 'serial' });

  test('власник передає січень виконавцю через майстер', async ({ browser }) => {
    const admin = await asUser(browser, Admin);
    await openDrawer(admin);

    await admin.getByRole('button', { name: /Assign month|Передать месяц/ }).click();
    // Mantine Chip ховає <input> поза в'юпортом (`check()` падає «outside of the viewport»): клікаємо по підпису чипа,
    // а що січень справді обрано - перевіряємо за станом прихованого чекбокса.
    await admin.getByRole('dialog').getByText(/^(January|Январь)$/).click();
    await expect(admin.getByRole('checkbox', { name: /January|Январь/ })).toBeChecked();
    await admin.getByTestId('wizard-next').click();

    await admin.getByLabel(/Find people|Найти людей/).fill(Executor.search);
    await admin.getByRole('button', { name: /E2E operator/ }).first().click({ timeout: 15_000 });
    await admin.getByTestId('wizard-next').click();

    await expect(admin.getByTestId('wizard-summary')).toBeVisible();
    await admin.getByTestId('wizard-apply').click();

    // Рядок «січень» у матриці, статус «Assigned».
    const months = admin.getByTestId('edit-assignments-months');
    await expect(months.getByRole('rowheader', { name: /January|Январь/ })).toBeVisible({ timeout: 20_000 });
    await expect(months).toContainText(/E2E operator/);
    await admin.context().close();
  });

  test('виконавець бачить смугу, ставить «Заповнено»; смуга змінюється на «чекає валідації»', async ({ browser }) => {
    const page = await asUser(browser, Executor);
    await page.goto(DocumentUrl);

    const strip = page.getByTestId('executor-strip');
    await expect(strip).toBeVisible({ timeout: 30_000 });
    await expect(strip).toContainText(/January|Январь/);

    await page.getByTestId('executor-strip-fill').click();
    await page.getByTestId('executor-strip-fill-confirm').click();

    await expect(page.getByTestId('executor-strip-fill')).toHaveCount(0, { timeout: 20_000 });
    await expect(page.getByTestId('executor-strip-row')).toHaveAttribute('data-status', 'Filled');
    await page.context().close();
  });

  test('власник бачить «Мої задачі → Awaiting my validation» і повертає місяць з коментарем', async ({ browser }) => {
    const admin = await asUser(browser, Admin);
    await admin.goto(DocumentUrl);

    await admin.getByRole('button', { name: /My tasks|Мои задачи/ }).click();
    await expect(admin.getByTestId('my-tasks-validation')).toBeVisible({ timeout: 30_000 });
    await admin.keyboard.press('Escape');

    await openDrawer(admin);
    await rowActions(admin, /Return|Вернуть/);
    const dialog = admin.getByRole('dialog', { name: /Return|Вернуть/ });
    await dialog.getByRole('textbox').fill('Check row 4');
    await dialog.getByRole('button', { name: /^(Return|Вернуть)$/ }).click();

    await expect(admin.getByTestId('edit-assignments-months')).toContainText(/Returned|Возвращено/, { timeout: 20_000 });
    await admin.context().close();
  });

  test('виконавець бачить причину повернення й заповнює знову; власник валідує', async ({ browser }) => {
    const executor = await asUser(browser, Executor);
    await executor.goto(DocumentUrl);
    await expect(executor.getByTestId('executor-strip')).toContainText('Check row 4', { timeout: 30_000 });
    await executor.getByTestId('executor-strip-fill').click();
    await executor.getByTestId('executor-strip-fill-confirm').click();
    await expect(executor.getByTestId('executor-strip-fill')).toHaveCount(0, { timeout: 20_000 });
    await executor.context().close();

    const admin = await asUser(browser, Admin);
    await openDrawer(admin);
    await rowActions(admin, /^(Validate|Проверить)$/);
    const dialog = admin.getByRole('dialog', { name: /Validate|Валидировать/ });
    await dialog.getByRole('button', { name: /^(Validate|Проверить)$/ }).click();

    await expect(admin.getByTestId('edit-assignments-months')).toContainText(/Validated|Проверено/, { timeout: 20_000 });
    await admin.context().close();
  });
});
