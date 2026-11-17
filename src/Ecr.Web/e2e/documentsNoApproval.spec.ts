import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { signIn } from './multiSheet';

/**
 * ✎ B147 (B75) проти РЕАЛЬНОГО сервера стенда (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep documentsNoApproval`):
 * документ, чия версія шаблону без подання й затвердження (`workflowMode = None`), у переліку документів — «No approval»,
 * а не чернетка:
 *  - смуга лічильників (рік, обраний проєкт): плитка «No approval» з числом 1, а «Draft» — 0;
 *  - клік по плитці — `state=NoApproval` в адресі, у полі фільтра й у запиті переліку; рядок документа — у переліку;
 *  - клітинка поточного місяця в рядку — «No approval» (знак і підпис), а не «Open»/чернетка.
 *
 * ⚠ Власні шаблон (режим None задається на ЧЕРНЕТЦІ версії до публікації), проєкт і документ — базову лінію стенда
 * файл не чіпає; облікові записи — наявні стенда (`e2e-admin`), нових не заводить.
 * ⛔ Без стенда файл ПАДАЄ (`globalSetup.ts`), а не пропускається (honesty-guard).
 */

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

interface NoApprovalDocument {
  readonly documentId: number;
  readonly businessKey: string;
  readonly projectId: number;
  readonly periodKey: number;
}

/** Шаблон з одним аркушем, версія «без погодження», активний проєкт поточного року і документ на ньому. */
async function noApprovalDocument(page: Page): Promise<NoApprovalDocument> {
  const stamp = String(Date.now());
  const template = await ok<{ templateId: number }>(
    await page.request.post('/api/v1/templates', { data: { code: `E2ENoAppr${stamp}`, nameL10n: { en: `E2E no approval ${stamp}` } } }),
    'шаблон',
  );
  const version = await ok<{ versionId: number }>(
    await page.request.post(`/api/v1/templates/${String(template.templateId)}/versions`, {
      data: { versionNumber: '1.0.0.0', cloneFromVersionId: null },
    }),
    'версія шаблону',
  );
  const base = `/api/v1/template-versions/${String(version.versionId)}`;
  await ok(
    await page.request.put(`${base}/sheets/S1`, {
      data: { nameL10n: { en: 'Log' }, ordinal: 1, isMandatory: true, isVisible: true, sheetGroup: null },
    }),
    'аркуш',
  );
  const table = await ok<{ id: number }>(
    await page.request.put(`${base}/sheets/S1/tables/T1`, {
      data: { nameL10n: { en: 'Table 1' }, ordinal: 0, layoutKind: 'Static', rowMode: 'Dynamic', maxDynamicRows: 50 },
    }),
    'таблиця',
  );
  await ok(
    await page.request.put(`${base}/tables/${String(table.id)}/columns/C1`, {
      data: {
        headerL10n: { en: 'C1' }, ordinal: 0, dataType: 'String', isRequired: false, isReadOnly: false, isHidden: false,
        precision: null, scale: null, defaultValue: null, displayFormat: null, lookupFilter: null,
        lookupRegistryDefId: null, styleId: null, unitId: null,
      },
    }),
    'стовпець',
  );
  // B75: режим — лише на чернетці версії, до публікації.
  const mode = await ok<{ workflowMode: string }>(
    await page.request.put(`${base}/workflow-mode`, { data: { workflowMode: 'None' } }),
    'режим без погодження',
  );
  expect(mode.workflowMode).toBe('None');
  await ok(await page.request.post(`${base}/publish`, { data: { reason: 'e2e: B147 document without approval' } }), 'публікація');

  const policies = await ok<{ id: number; code: string }[]>(await page.request.get('/api/v1/projects/period-policies'), 'політики');
  const policy = policies.find((item) => item.code === 'ECR-Standard') ?? policies[0];
  expect(policy, 'у стенда немає жодної політики періодів').toBeDefined();
  const project = await ok<{ projectId: number }>(
    await page.request.post('/api/v1/projects', {
      data: {
        code: `E2ENoApprPrj${stamp}`,
        nameL10n: { en: `E2E no approval project ${stamp}` },
        periodKind: 'Monthly',
        timeZoneId: 'UTC',
        templateVersionId: version.versionId,
        periodPolicyId: policy?.id,
        year: new Date().getUTCFullYear(),
      },
    }),
    'проєкт',
  );
  await ok(await page.request.post(`/api/v1/projects/${String(project.projectId)}/activate`), 'активація проєкту');
  const structure = await ok<{ sheets: { id: number }[] }>(await page.request.get(`${base}/structure`), 'структура версії');
  const created = await ok<{ documentId: number }>(
    await page.request.post('/api/v1/documents', {
      data: { projectId: project.projectId, sheetDefIds: structure.sheets.map((sheet) => sheet.id), templateVersionId: version.versionId },
    }),
    'документ',
  );
  const calendar = await ok<{ periods: { periodKey: number; state: string; isCurrent: boolean }[] }>(
    await page.request.get(`/api/v1/projects/${String(project.projectId)}/periods`),
    'календар проєкту',
  );
  const period = calendar.periods.find((item) => item.isCurrent) ?? calendar.periods.find((item) => item.state === 'Open');
  expect(period, 'у проєкту немає відкритого місяця').toBeDefined();
  const card = await ok<{ businessKey: string; workflowMode?: string | null }>(
    await page.request.get(`/api/v1/documents/${String(created.documentId)}?periodKey=${String(period?.periodKey)}`),
    'картка документа',
  );
  // Контракт PE: картка за місяць несе режим версії саме цього місяця.
  expect(card.workflowMode).toBe('None');

  return { documentId: created.documentId, businessKey: card.businessKey, projectId: project.projectId, periodKey: period?.periodKey ?? 0 };
}

test.describe('B147: document list — «No approval» state', () => {
  test('strip tile, click-through filter, row and year cell show No approval, not Draft', async ({ page }) => {
    test.setTimeout(240_000);
    await signIn(page);
    const doc = await noApprovalDocument(page);
    const year = Math.trunc(doc.periodKey / 100);

    await page.goto(`/?year=${String(year)}&projectId=${String(doc.projectId)}`);

    const strip = page.getByRole('group', { name: 'Document counters' }).or(page.locator('[data-summary-strip]'));
    const tile = page.locator('[data-summary-strip] [data-summary-counter="noApproval"]');
    await expect(tile, 'плитки «No approval» немає на смузі').toBeVisible({ timeout: 60_000 });
    await expect(tile).toContainText('1');
    await expect(tile).toContainText('No approval');
    // Документ поза п'ятьма станами: «Draft» його не рахує.
    await expect(strip.locator('[data-summary-counter="draft"]').first()).toContainText('0');

    // Рядок документа: клітинка поточного місяця — «No approval» (знак і підпис), а не «Open».
    const row = page.locator(`[data-document-row="${doc.businessKey}"]`);
    await expect(row).toBeVisible({ timeout: 60_000 });
    const cell = row.locator(`a[data-month="${String(doc.periodKey)}"]`);
    await expect(cell).toHaveAttribute('data-cell', 'noApproval');
    await expect(cell).toHaveAttribute('aria-label', /No approval/);
    await expect(cell).toHaveText('–');

    // Клік по плитці — фільтр state=NoApproval: адреса, поле фільтра, запит переліку; рядок лишається.
    const listRequest = page.waitForRequest(
      (request) => request.url().includes('/api/v1/documents?') && new URL(request.url()).searchParams.get('state') === 'NoApproval',
    );
    await tile.click();
    await listRequest;
    await expect(page).toHaveURL(/[?&]state=NoApproval\b/);
    await expect(tile).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('combobox', { name: 'State' })).toHaveValue('NoApproval');
    await expect(row).toBeVisible({ timeout: 60_000 });

    // Фільтр «Draft» того самого проєкту документа не показує.
    await page.getByRole('combobox', { name: 'State' }).selectOption('Draft');
    await expect(page).toHaveURL(/[?&]state=Draft\b/);
    await expect(row).toHaveCount(0, { timeout: 60_000 });
  });
});
