import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { createMultiSheetDocument, signIn } from './multiSheet';

/**
 * ✎ Period clarity (квіз людини 07.11) проти РЕАЛЬНОГО стенда (`tools/e2e-stand.ps1`, workflow `e2e-stand`,
 * `-Grep "Period clarity"`):
 *  1. Periods — лише блокування місяців: рядок місяця зі станом і датою «Locks on», без погоджень перерахунку
 *     закритого періоду і без дій проєкту (маршрут, клон, архів, перерахунок);
 *  2. Project settings — «Pin…» поточного місяця і «Back to automatic» (нові `…/settings/current-period`);
 *  3. Documents — типово рік: смуга 12 місяців, клік по місяцю відкриває документ на ньому, фільтр стану й
 *     лічильники за рік;
 *  4. майстер нового документа — без кроку періоду.
 *
 * ⛔ Без стенда файл ПАДАЄ (`globalSetup.ts`), а не пропускається (honesty-guard).
 * ⚠ Проєкт і документ — власні (`createMultiSheetDocument`), базову лінію стенда файл не чіпає.
 * ⚠ Написано без локального прогону (правило «e2e — на CI»): перший живий прогін — CI.
 */

interface DocumentTable {
  sheetDefId: number;
  sheetCode: string;
}

interface DocumentCard {
  projectId: number;
  businessKey: string;
}

interface Project {
  id: number;
  code: string;
}

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

/** Власний документ на трьох аркушах; перший аркуш поданий у відкритому місяці — місяць «частково подано». */
async function partlySubmittedDocument(page: Page): Promise<{ id: number; periodKey: number; card: DocumentCard; project: Project }> {
  const { documentId, periodKey } = await createMultiSheetDocument(page, 3, 1);
  const id = String(documentId);
  const tables = await ok<DocumentTable[]>(
    await page.request.get(`/api/v1/documents/${id}/tables?periodKey=${String(periodKey)}`),
    'GET tables',
  );
  const first = tables[0];
  expect(first, 'документ без аркушів').toBeDefined();
  await ok(
    await page.request.post(`/api/v1/documents/${id}/submit`, {
      data: { sheetDefId: first?.sheetDefId, periodKey, acknowledgeWarnings: true },
    }),
    'submit',
  );

  const card = await ok<DocumentCard>(await page.request.get(`/api/v1/documents/${id}?periodKey=${String(periodKey)}`), 'GET document');
  const projects = await ok<{ items: Project[] }>(await page.request.get('/api/v1/projects?limit=200'), 'GET projects');
  const project = projects.items.find((item) => item.id === card.projectId);
  expect(project, 'проєкт документа не видно в переліку').toBeDefined();

  return { id: documentId, periodKey, card, project: project as Project };
}

test.describe('Period clarity', () => {
  test.describe.configure({ mode: 'serial', timeout: 300_000 });

  test('periods page shows month rows with status and lock date and no recalculation approvals', async ({ page }) => {
    await signIn(page);
    const { card, periodKey } = await partlySubmittedDocument(page);

    await page.goto(`/admin/periods?projectId=${String(card.projectId)}`);
    const table = page.locator('[data-periods-table="months"]');
    await expect(table).toBeVisible({ timeout: 60_000 });

    const row = table.locator(`[data-period-row="${String(periodKey)}"]`);
    await expect(row).toBeVisible();
    // Підпис — назва місяця, ключ — лише в підказці.
    await expect(row.locator('[data-period-caption]')).not.toHaveText(String(periodKey));
    await expect(row).toContainText(/Open|Late \(flagged\)|Locked/);
    // Одна дата блокування в рядку.
    await expect(row.locator(`[data-period-locks-on="${String(periodKey)}"] time`)).toHaveCount(1);
    await expect(row.locator('time')).toHaveCount(1);

    // Сторінка лише блокує місяці: ні погоджень перерахунку закритого періоду, ні дій проєкту.
    await expect(page.getByText(/recalculation approv/i)).toHaveCount(0);
    for (const name of [/^Recalculate$/, /^Archive$/, /^Clone/, /^Approval route/]) {
      await expect(page.getByRole('button', { name })).toHaveCount(0);
    }
    await expect(page.locator('[data-periods-lock-note]')).toBeVisible();
    await expect(page.locator('[data-project-settings-link]')).toHaveAttribute(
      'href',
      `/admin/projects?projectId=${String(card.projectId)}`,
    );
  });

  test('project settings pins the current month and goes back to automatic', async ({ page }) => {
    await signIn(page);
    const { card } = await partlySubmittedDocument(page);

    await page.goto(`/admin/projects?projectId=${String(card.projectId)}`);
    const section = page.locator('[data-project-section="current-period"]');
    await expect(section.locator('[data-current-period-mode="Auto"]')).toBeVisible({ timeout: 60_000 });
    await expect(section.locator('[data-current-period-badge]')).toHaveText(/Automatic by date/);

    await section.getByRole('button', { name: /^Pin/ }).click();
    const dialog = page.getByRole('dialog');
    await dialog.getByRole('textbox', { name: /Month to pin/ }).click();
    await page.getByRole('listbox', { name: /Month to pin/ }).getByRole('option').first().click();
    await dialog.getByRole('textbox', { name: /Reason/ }).fill('e2e: period clarity pin');
    await dialog.getByRole('button', { name: /^Make current$/ }).click();

    await expect(section.locator('[data-current-period-mode="Pinned"]')).toBeVisible({ timeout: 60_000 });
    await expect(section.locator('[data-current-period-badge]')).toHaveText(/Pinned to/);

    await section.getByRole('button', { name: /^Back to automatic$/ }).click();
    await expect(section.locator('[data-current-period-mode="Auto"]')).toBeVisible({ timeout: 60_000 });
    await expect(section.locator('[data-current-period-badge]')).toHaveText(/Automatic by date/);
  });

  test('documents show a year strip, a month opens the document and the state filter works for the year', async ({ page }) => {
    await signIn(page);
    const { id, periodKey, card } = await partlySubmittedDocument(page);
    const year = Math.trunc(periodKey / 100);

    await page.goto(`/?projectId=${String(card.projectId)}&year=${String(year)}`);
    await expect(page.locator('[data-documents-year-note]')).toBeVisible({ timeout: 60_000 });
    const row = page.locator(`[data-document-row="${card.businessKey}"]`);
    await expect(row).toBeVisible({ timeout: 60_000 });

    const cells = row.locator('[data-month]');
    await expect(cells).toHaveCount(12);
    const month = row.locator(`[data-month="${String(periodKey)}"]`);
    await expect(month).toHaveAttribute('data-month-state', 'PartlySubmitted');
    await expect(month).toHaveAttribute('aria-label', /: Partly submitted$/);

    // Лічильники смуги — за рік.
    await expect(page.locator('[data-summary-counter="partlySubmitted"]')).toBeVisible({ timeout: 60_000 });

    // Фільтр стану за рік.
    await page.getByRole('combobox', { name: /^State$/ }).selectOption('PartlySubmitted');
    await expect(page).toHaveURL(/state=PartlySubmitted/);
    await expect(page).toHaveURL(new RegExp(`year=${String(year)}`));
    await expect(row).toBeVisible({ timeout: 60_000 });
    await page.getByRole('combobox', { name: /^State$/ }).selectOption('Approved');
    await expect(row).toHaveCount(0, { timeout: 60_000 });
    await page.getByRole('combobox', { name: /^State$/ }).selectOption('');

    // Клік по місяцю — документ на цьому місяці.
    await expect(row).toBeVisible({ timeout: 60_000 });
    await row.locator(`[data-month="${String(periodKey)}"]`).click();
    await expect(page).toHaveURL(new RegExp(`/documents/${String(id)}\\?periodKey=${String(periodKey)}`));
  });

  test('new document wizard has no period step', async ({ page }) => {
    await signIn(page);
    const { project } = await partlySubmittedDocument(page);

    await page.goto(`/?projectId=${String(project.id)}`);
    await page.getByRole('button', { name: /^New document$/ }).click();
    const dialog = page.getByRole('dialog');
    await dialog.getByRole('textbox', { name: /^Project/ }).click();
    // ⚠ У listbox поля, а не будь-який `option`: фільтр «Project» переліку — нативний `<select>` із тими самими кодами.
    await page.getByRole('listbox', { name: /Project/ }).getByRole('option', { name: project.code, exact: true }).click();
    await dialog.getByRole('button', { name: /^Next$/ }).click();

    // Одразу аркуші — кроку періоду немає.
    await expect(dialog.getByText(/Leave out sheets that do not apply/)).toBeVisible({ timeout: 60_000 });
    await expect(dialog.getByRole('textbox', { name: /^Period/ })).toHaveCount(0);
    await dialog.getByRole('button', { name: /^Next$/ }).click();

    const created = page.waitForRequest((request) => request.method() === 'POST' && /\/api\/v1\/documents$/.test(request.url()));
    await dialog.getByRole('button', { name: /^Create document$/ }).click();
    const body = (await created).postDataJSON() as Record<string, unknown>;
    expect(body).not.toHaveProperty('periodKey');
    await expect(page).toHaveURL(/\/documents\/\d+/, { timeout: 60_000 });
  });
});
