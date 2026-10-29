import { expect, test, type Page } from '@playwright/test';

/**
 * Журнали аудиту проти РЕАЛЬНОГО сервера стенда (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep auditSortLive`):
 *
 *  - після правки комірки ПЕРШИЙ рядок журналу — ця правка (типовий порядок «найновіші першими»), а не найстарша
 *    у вікні;
 *  - заголовок «Changed at» перемикає порядок на сервері (`aria-sort`, перший рядок стає найстаршим у вікні);
 *  - «Експорт CSV» є на всіх вкладках (комірки, структура, безпека, погодження) і віддає файл.
 *
 * ⛔ Без стенда файл ПАДАЄ, а не пропускається (honesty-guard, `globalSetup.ts`).
 * ⚠ Написано без локального прогону (на машині автора немає стенда): перший живий прогін — CI.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

test.describe('Журнали аудиту: порядок і експорт на реальному стенді', () => {
  test.beforeAll(() => {
    if (PeriodKey === '' || DocumentId === '') {
      throw new Error('стенда немає: в оточенні немає ECR_E2E_PERIOD/ECR_E2E_DOCUMENT (tools/e2e-stand.ps1)');
    }
  });

  test.describe.configure({ timeout: 240_000 });

  test('після правки перший рядок — вона; заголовок перемикає порядок на сервері', async ({ page, context }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    const value = String(500 + Math.floor(Math.random() * 400));

    await signIn(page);
    await makeSheetEditable(page);
    await pasteFirstCell(page, value);

    await page.goto(`/admin/audit?documentId=${DocumentId}`);
    const rows = page.locator('table.ecr-audit-table tbody tr');
    const firstBecomes = rows.first().locator('[data-audit-becomes]');
    await expect(firstBecomes, 'першим рядком журналу має бути остання правка').toHaveText(value, { timeout: 30_000 });

    const when = page.locator('th[data-audit-sort-column="time"]');
    await expect(when).toHaveAttribute('aria-sort', 'descending');

    // Перемикання — запит на сервер із sort=ChangedAtAsc, а не перестановка сторінки на клієнті.
    const request = page.waitForRequest((r) => r.url().includes('/api/v1/audit/cells') && r.url().includes('sort=ChangedAtAsc'));
    await when.getByRole('button').click();
    await request;
    await expect(when).toHaveAttribute('aria-sort', 'ascending');
    await expect(page).toHaveURL(/sort=ChangedAtAsc/);

    // Назад у типовий: параметр зникає з адреси, перший рядок знову остання правка.
    await when.getByRole('button').click();
    await expect(when).toHaveAttribute('aria-sort', 'descending');
    await expect(rows.first().locator('[data-audit-becomes]')).toHaveText(value, { timeout: 30_000 });
  });

  for (const tab of ['cells', 'structure', 'security', 'workflow'] as const) {
    test(`вкладка ${tab}: кнопка «Export CSV» є і віддає файл`, async ({ page }) => {
      await signIn(page);
      await page.goto(tab === 'cells' ? '/admin/audit' : `/admin/audit?view=${tab}`);

      const button = page.locator(`[data-audit-export="${tab}"]`);
      await expect(button).toBeVisible({ timeout: 30_000 });

      const download = page.waitForEvent('download', { timeout: 60_000 });
      await button.click();
      const file = await download;
      expect(file.suggestedFilename()).toMatch(new RegExp(`^audit-${tab}-.*\\.csv$`));
    });
  }
});

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

/** Аркуш у Draft (та сама дія, що в `auditTrail.spec.ts`): подані/затверджені аркуші не правляться. */
async function makeSheetEditable(page: Page): Promise<void> {
  await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
  const activeTab = page.getByRole('tab', { selected: true });
  await expect(activeTab, 'у документа немає жодного аркуша').toBeVisible({ timeout: 30_000 });

  const state = (await activeTab.textContent()) ?? '';
  if (/Submitted|Approved/.test(state)) {
    await page.getByRole('button', { name: /Return for edits|Повернути/i }).first().click();
    const dialog = page.getByRole('dialog');
    await dialog.getByRole('textbox', { name: /Reason|Причина/i }).fill('e2e: порядок журналу аудиту');
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
}
