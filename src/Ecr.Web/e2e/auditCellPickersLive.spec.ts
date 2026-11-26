import { expect, test, type Page } from '@playwright/test';

/**
 * audit-filters-live D2/D3 (AC-3, AC-4) — журнал змін комірок проти РЕАЛЬНОГО сервера стенда (`tools/e2e-stand.ps1`,
 * workflow `e2e-stand`, `-Grep auditCellPickersLive`):
 *
 *  - «Document» — вибір документа за кодом/назвою: набраний бізнес-ключ ШУКАЄ документ (`GET /documents?q=`), а не
 *    стає номером із цифр ключа (live 25.11: «P1-V9-0001» → `190001` → «Document not found»); обраний пункт ставить у
 *    адресу Id документа;
 *  - «Column» — вибір колонки: набраний «M10» не стає `columnDefId=10` (live 25.11: хибне «No changes in this window»).
 *
 * ⛔ Без стенда файл ПАДАЄ, а не пропускається (honesty-guard, `globalSetup.ts`).
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

test.describe('Аудит комірок: Document/Column вибором на реальному стенді', () => {
  test.beforeAll(() => {
    if (DocumentId === '') {
      throw new Error('стенда немає: в оточенні немає ECR_E2E_DOCUMENT (tools/e2e-stand.ps1)');
    }
  });

  test.describe.configure({ timeout: 180_000 });

  test('Document: бізнес-ключ шукає й обирає документ, у адресі — Id', async ({ page }) => {
    await signIn(page);

    const list = await page.request.get('/api/v1/documents?limit=200');
    expect(list.ok(), 'перелік документів стенда').toBe(true);
    const items = ((await list.json()) as { items: { id: number; businessKey: string }[] }).items;
    const doc = items.find((item) => String(item.id) === DocumentId);
    expect(doc, `документ ${DocumentId} у переліку стенда`).toBeDefined();
    const key = doc?.businessKey ?? '';

    const digits = key.replace(/\D/gu, '');
    const wrong: string[] = [];
    page.on('request', (request) => {
      const url = new URL(request.url());
      if (url.pathname === '/api/v1/audit/cells' && digits !== '' && url.searchParams.get('documentId') === String(Number(digits))) {
        wrong.push(request.url());
      }
    });

    await page.goto('/admin/audit');
    const field = page.getByLabel('Document', { exact: true }).and(page.locator('input'));
    await expect(field).toBeVisible({ timeout: 30_000 });

    const searched = page.waitForRequest(
      (request) => request.url().includes('/api/v1/documents?') && new URL(request.url()).searchParams.get('q') === key,
    );
    await field.fill(key);
    await searched;
    await expect(page).not.toHaveURL(/documentId=/u);

    await page.getByRole('option', { name: new RegExp(key.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&'), 'u') }).first().click();
    await expect(page).toHaveURL(new RegExp(`documentId=${DocumentId}(&|$)`, 'u'));
    expect(wrong, 'набраний ключ не мав ставати номером документа').toEqual([]);
  });

  // ✎ AC-9 (рішення людини 26.11): «Clear filters» знімає все разом і є на кожній вкладці.
  test('Clear filters: знімає документ, дати й решту фільтрів на кожній вкладці', async ({ page }) => {
    await signIn(page);
    await page.goto(`/admin/audit?from=2026-11-01&to=2026-11-10&documentId=${DocumentId}&rowKey=R1&origin=UserEdit&lateOnly=true`);
    await page.locator('[data-audit-clear-filters]').click();
    await expect(page).toHaveURL(/\/admin\/audit$/u);

    for (const [view, filters] of [
      ['structure', 'entityType=TableDef'],
      ['security', 'eventType=LoginFailed'],
      ['workflow', 'action=Approve'],
    ] as const) {
      await page.goto(`/admin/audit?view=${view}&from=2026-11-01&to=2026-11-10&${filters}&changedBy=1`);
      await page.locator('[data-audit-clear-filters]').click();
      await expect(page).toHaveURL(new RegExp(`/admin/audit\\?view=${view}$`, 'u'));
    }
  });

  test('Column: набраний «M10» не стає колонкою 10', async ({ page }) => {
    await signIn(page);
    await page.goto(`/admin/audit?documentId=${DocumentId}`);

    const field = page.getByLabel('Column', { exact: true }).and(page.locator('input'));
    await expect(field).toBeVisible({ timeout: 30_000 });
    await field.fill('M10');
    await page.waitForTimeout(1_000);
    await expect(page).not.toHaveURL(/columnDefId=/u);
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
