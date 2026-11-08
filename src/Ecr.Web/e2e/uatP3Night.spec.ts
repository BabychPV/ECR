import { expect, test, type Page } from '@playwright/test';
import { chooseSubmitPeriod } from './submitPeriod';

/**
 * Три дрібні пункти людського проходу W15k (lane `integ-uat-p3-night`) — на справжньому стенді
 * (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep uatP3Night`):
 *
 *  1. «Submit: <місяць>» питає підтвердження з назвою місяця; Cancel і Esc нічого не подають і повертають фокус на кнопку;
 *  2. комірка закріпленого рядка підсумків позначена «лише для читання» (замок/стан) і пояснює чому підказкою;
 *  3. лічильники переліку документів і огляду кампанії називають свою область (період і проєкти).
 *
 * ⚠ Подання тут НЕ відбувається: POST `/submit` перехоплено й лічиться — базова лінія стенда не змінюється.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';
const DocumentUrl = `/documents/${DocumentId}?periodKey=${PeriodKey}`;

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

async function openDocument(page: Page): Promise<void> {
  await page.goto(DocumentUrl);
  await expect(page.getByTestId('document-actions'), 'у документа немає рядка дій').toBeVisible({ timeout: 60_000 });
  await expect(page.getByTestId('document-actions-pending')).toHaveCount(0, { timeout: 30_000 });
}

test.describe('uatP3Night Submit asks for confirmation naming the month', () => {
  test('Cancel and Escape submit nothing and return focus to the Submit button', async ({ page }) => {
    test.skip(DocumentId === '' || PeriodKey === '', 'стенд не задав ECR_E2E_DOCUMENT / ECR_E2E_PERIOD');
    await signIn(page);
    await openDocument(page);

    const posts: string[] = [];
    await page.route(`**/api/v1/documents/${DocumentId}/submit`, async (route) => {
      posts.push(route.request().postData() ?? '');
      await route.fulfill({ status: 204, body: '' });
    });

    const submit = await chooseSubmitPeriod(page, Number(PeriodKey));
    const label = ((await submit.textContent()) ?? '').replace(/^[^:]*:\s*/, '').trim();
    const dialog = page.getByRole('dialog');

    await submit.click();
    await expect(dialog, 'подання не спитало підтвердження').toBeVisible({ timeout: 15_000 });
    await expect(dialog, 'підтвердження не називає місяць').toContainText(label);
    await dialog.getByRole('button', { name: /Cancel|Скасувати|Отмена|Болдырмау/i }).click();
    await expect(dialog).toHaveCount(0, { timeout: 10_000 });
    await expect(submit, 'після Cancel фокус не на кнопці подання').toBeFocused();

    await submit.click();
    await expect(dialog).toBeVisible({ timeout: 15_000 });
    await page.keyboard.press('Escape');
    await expect(dialog).toHaveCount(0, { timeout: 10_000 });
    await expect(submit, 'після Esc фокус не на кнопці подання').toBeFocused();

    expect(posts, 'Cancel/Esc надіслали подання').toEqual([]);

    await submit.click();
    await dialog.getByRole('button', { name: /^(Submit|Подать|Подати|Тапсыру)$/ }).click();
    await expect.poll(() => posts.length, { message: 'підтвердження не надіслало подання', timeout: 30_000 }).toBe(1);
    expect(posts[0]).toContain(`"periodKey":${PeriodKey}`);
  });
});

test.describe('uatP3Night Total row cells are read only and say why', () => {
  test('every cell of the pinned total row carries the read only state and a reason', async ({ page }) => {
    test.skip(DocumentId === '' || PeriodKey === '', 'стенд не задав ECR_E2E_DOCUMENT / ECR_E2E_PERIOD');
    await signIn(page);
    await openDocument(page);

    const grid = page.locator('revo-grid').first();
    await expect(grid.locator('revogr-data .rgCell').first(), 'рядки сітки не завантажилися').toBeVisible({ timeout: 60_000 });
    const totals = page.locator('.rgCell[data-grid-totals]');
    test.skip((await totals.count()) === 0, 'у першої таблиці стенда немає рядка підсумків');

    const count = await totals.count();
    for (let index = 0; index < count; index += 1) {
      const cell = totals.nth(index);
      await expect(cell).toHaveAttribute('data-cell-state', 'readOnly');
      await expect(cell).toHaveClass(/ecr-cell--read-only/);
      await expect(cell).toHaveAttribute('title', /\S/);
    }

    // Набір у комірку підсумків не відкриває редактора.
    await totals.last().click();
    await page.keyboard.type('7');
    await expect(page.locator('revo-grid revogr-edit input')).toHaveCount(0);
  });
});

test.describe('uatP3Night Counters name the period and projects they count', () => {
  test('Documents strip and campaign overview show their scope labels', async ({ page }) => {
    test.skip(PeriodKey === '', 'стенд не задав ECR_E2E_PERIOD');
    await signIn(page);

    await page.goto(`/?periodKey=${PeriodKey}`);
    const scope = page.locator('[data-summary-scope]');
    await expect(scope, 'смуга лічильників не називає область').toBeVisible({ timeout: 60_000 });
    await expect(scope).toHaveText(/\d{4}/);

    await page.goto(`/admin/campaign?periodKey=${PeriodKey}`);
    const campaign = page.locator('[data-campaign-scope]');
    await expect(campaign, 'огляд кампанії не називає область').toBeVisible({ timeout: 60_000 });
    await expect(campaign).toHaveText(/\d{4}/);
  });
});
