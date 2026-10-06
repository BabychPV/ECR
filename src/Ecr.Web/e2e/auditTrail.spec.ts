import { expect, test, type Page } from '@playwright/test';

/**
 * UI-38: журнал змін комірок у формі макета (`docs/design/hybrid/screens-ops.js`, `/admin/audit`):
 * дві правки однієї комірки → у журналі рядок «Was → becomes» зі старим значенням закресленим, новим
 * поруч і походженням словом («Typed by a user»), а не кодом `UserEdit`.
 *
 * ⚠ Ганяє «Аудит» на стенді (`tools/e2e-stand.ps1`); без стенда — пропуск під `ECR_E2E_OPTIONAL`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

test.describe('Журнал змін: Was → becomes (UI-38)', () => {
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );

  test('дві правки комірки — у журналі старе закреслене, нове поруч, походження словом', async ({ page, context }) => {
    test.setTimeout(180_000);
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);

    // Два різні цілі значення, щоб рядок журналу знаходився однозначно і в повторних прогонах.
    const before = String(100 + Math.floor(Math.random() * 400));
    const after = String(Number(before) + 7);

    await signIn(page);
    await makeSheetEditable(page);
    await pasteFirstCell(page, before);
    await pasteFirstCell(page, after);

    await page.goto(`/admin/audit?documentId=${DocumentId}`);

    const pair = page
      .locator('[data-audit-was-becomes]')
      .filter({ has: page.locator('[data-audit-was]', { hasText: new RegExp(`^${before}$`) }) })
      .filter({ has: page.locator('[data-audit-becomes]', { hasText: new RegExp(`^${after}$`) }) })
      .first();
    await expect(pair, 'у журналі немає рядка «було → стало» для двох правок').toBeVisible({ timeout: 30_000 });

    // ⛔ Старе значення закреслене (макет `.was{text-decoration:line-through}`).
    const decoration = await pair.locator('[data-audit-was]').evaluate((el) => getComputedStyle(el).textDecorationLine);
    expect(decoration).toContain('line-through');

    // Походження — словом, а не кодом сервера.
    const row = pair.locator('xpath=ancestor::tr[1]');
    await expect(row.locator('[data-audit-origin="UserEdit"]')).toHaveText('Typed by a user');
    await expect(row).not.toContainText('UserEdit');
  });
});

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

/** Аркуш у Draft (та сама дія, що в `security.spec.ts`): подані/затверджені аркуші не правляться. */
async function makeSheetEditable(page: Page): Promise<void> {
  await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
  const activeTab = page.getByRole('tab', { selected: true });
  await expect(activeTab, 'у документа немає жодного аркуша').toBeVisible({ timeout: 30_000 });

  const state = (await activeTab.textContent()) ?? '';
  if (/Submitted|Approved/.test(state)) {
    await page.getByRole('button', { name: /Return for edits|Повернути/i }).first().click();
    const dialog = page.getByRole('dialog');
    await dialog.getByRole('textbox', { name: /Reason|Причина/i }).fill('e2e: журнал змін UI-38');
    await dialog.getByRole('button', { name: /Return for edits|Повернути/i }).click();
    await expect(activeTab, 'аркуш не повернувся в Draft').toContainText('Draft', { timeout: 15_000 });
  }

  await expect(page.locator('revo-grid').first(), "сітка документа не з'явилася").toBeVisible({ timeout: 30_000 });
}

/** Вставка в першу комірку тіла сітки і підтвердження сервера (`data-save-status="saved"`). */
async function pasteFirstCell(page: Page, value: string): Promise<void> {
  await page.locator('revo-grid').first().locator('revogr-data .rgCell').first().click();
  await page.evaluate(async (text) => {
    await navigator.clipboard.writeText(text);
  }, value);
  await page.keyboard.press('Control+v');
  await expect(page.locator('[data-save-status="saved"]'), 'сервер не підтвердив збереження').toBeVisible({ timeout: 15_000 });
  // Позначка «saved» зникає сама за ~2 с; наступна вставка чекає на нову.
  await expect(page.locator('[data-save-status="saved"]')).toHaveCount(0, { timeout: 10_000 });
}
