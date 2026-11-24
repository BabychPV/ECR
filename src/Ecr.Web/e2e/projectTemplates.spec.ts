import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { signIn } from './multiSheet';

/**
 * ✎ B74 P4 (D-576, рішення людини 13.11): проєкт «рік · об'єкт» з КІЛЬКОМА шаблонами — проти РЕАЛЬНОГО стенда
 * (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep "B74"`):
 *  1. налаштування проєкту → «Templates»: основний шаблон з позначкою «Primary», «Detach» для нього недоступний
 *     (і сервер відмовляє `409 ECR-PRD-0409`), «Add template» приєднує другий шаблон — він у переліку;
 *  2. майстер «New document»: крок Template пропонує шаблони проєкту; документ за ДРУГИМ шаблоном — аркуші цього
 *     шаблону, `templateId` у запиті, код шаблону в бізнес-ключі й у колонці «Template» переліку.
 *
 * ⚠ Передумова «другий шаблон на стенді» не береться на віру: обидва шаблони, проєкт і документ — власні, заводяться
 * через API (як `multiSheet.ts`). Базову лінію стенда файл не чіпає.
 * ⚠ Написано без локального прогону (правило «e2e — на CI»): перший живий прогін — CI.
 */

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

interface Published {
  readonly code: string;
  readonly templateId: number;
  readonly versionId: number;
  readonly sheetName: string;
}

/** Опублікований шаблон з одним аркушем (одна таблиця, один стовпець). */
async function publishedTemplate(page: Page, code: string, sheetName: string): Promise<Published> {
  const template = await ok<{ templateId: number }>(
    await page.request.post('/api/v1/templates', { data: { code, nameL10n: { en: `E2E B74 ${code}` } } }),
    `шаблон ${code}`,
  );
  const version = await ok<{ versionId: number }>(
    await page.request.post(`/api/v1/templates/${String(template.templateId)}/versions`, {
      data: { versionNumber: '1.0.0.0', cloneFromVersionId: null },
    }),
    `версія ${code}`,
  );
  const base = `/api/v1/template-versions/${String(version.versionId)}`;
  await ok(
    await page.request.put(`${base}/sheets/S1`, {
      data: { nameL10n: { en: sheetName }, ordinal: 1, isMandatory: true, isVisible: true, sheetGroup: null },
    }),
    `аркуш ${code}`,
  );
  const table = await ok<{ id: number }>(
    await page.request.put(`${base}/sheets/S1/tables/T1`, {
      data: { nameL10n: { en: 'Table 1' }, ordinal: 0, layoutKind: 'Static', rowMode: 'Dynamic', maxDynamicRows: 50 },
    }),
    `таблиця ${code}`,
  );
  await ok(
    await page.request.put(`${base}/tables/${String(table.id)}/columns/C1`, {
      data: {
        headerL10n: { en: 'C1' }, ordinal: 0, dataType: 'String', isRequired: false, isReadOnly: false, isHidden: false,
        precision: null, scale: null, defaultValue: null, displayFormat: null, lookupFilter: null,
        lookupRegistryDefId: null, styleId: null, unitId: null,
      },
    }),
    `стовпець ${code}`,
  );
  await ok(await page.request.post(`${base}/publish`, { data: { reason: 'e2e: B74 project templates' } }), `публікація ${code}`);

  return { code, templateId: template.templateId, versionId: version.versionId, sheetName };
}

/** Активний місячний проєкт з ОДНИМ (основним) шаблоном. */
async function projectWith(page: Page, code: string, primary: Published): Promise<number> {
  const policies = await ok<{ id: number; code: string }[]>(await page.request.get('/api/v1/projects/period-policies'), 'політики');
  const policy = policies.find((item) => item.code === 'ECR-Standard') ?? policies[0];
  expect(policy, 'у стенда немає жодної політики періодів').toBeDefined();
  const created = await ok<{ projectId: number }>(
    await page.request.post('/api/v1/projects', {
      data: {
        code,
        nameL10n: { en: `E2E B74 ${code}` },
        periodKind: 'Monthly',
        timeZoneId: 'UTC',
        templateVersionId: primary.versionId,
        periodPolicyId: policy?.id,
        year: new Date().getUTCFullYear(),
      },
    }),
    'проєкт',
  );
  await ok(await page.request.post(`/api/v1/projects/${String(created.projectId)}/activate`), 'активація проєкту');

  return created.projectId;
}

test.describe('B74 project templates', () => {
  test.describe.configure({ mode: 'serial', timeout: 300_000 });

  test('B74: project settings Templates tab attaches a second template and refuses to detach the primary', async ({ page }) => {
    await signIn(page);
    const stamp = String(Date.now());
    const primary = await publishedTemplate(page, `E2EB74G${stamp}`, 'General sheet');
    const second = await publishedTemplate(page, `E2EB74L${stamp}`, 'Land plots sheet');
    const projectId = await projectWith(page, `E2EB74Prj${stamp}`, primary);

    await page.goto(`/projects/${String(projectId)}/settings`);
    const section = page.locator('[data-project-section="templates"]');
    await expect(section.getByTestId('project-templates')).toBeVisible({ timeout: 60_000 });

    // Основний — з позначкою «Primary»; «Detach» для нього недоступний (кнопка не відкриває підтвердження).
    const primaryRow = section.locator(`[data-project-template="${primary.code}"]`);
    await expect(primaryRow.getByTestId('project-template-primary')).toHaveText('Primary');
    const primaryDetach = primaryRow.getByRole('button', { name: `Detach ${primary.code}` });
    await expect(primaryDetach).toHaveAttribute('aria-disabled', 'true');
    await primaryDetach.click({ force: true });
    await expect(page.getByRole('dialog')).toHaveCount(0);

    // Сервер відмовляє так само — від'єднати основний шаблон не можна навіть в обхід інтерфейсу.
    const refused = await page.request.delete(`/api/v1/projects/${String(projectId)}/templates/${String(primary.templateId)}`);
    expect(refused.status()).toBe(409);
    expect(await refused.text()).toContain('ECR-PRD-0409');

    // «Add template» → другий шаблон, остання опублікована версія підставлена сама.
    await section.getByRole('button', { name: 'Add template' }).click();
    const dialog = page.getByRole('dialog', { name: 'Add template' });
    await expect(dialog).toBeVisible({ timeout: 10_000 });
    await dialog.getByRole('textbox', { name: /^Template/ }).fill(second.code);
    await page.getByRole('listbox', { name: /Template/ }).getByRole('option', { name: new RegExp(`^${second.code}`) }).click();
    await expect(dialog.getByRole('textbox', { name: /^Version/ })).toHaveValue('1.0.0.0', { timeout: 30_000 });
    await dialog.getByRole('button', { name: 'Add', exact: true }).click();
    await expect(dialog).toBeHidden({ timeout: 30_000 });

    const secondRow = section.locator(`[data-project-template="${second.code}"]`);
    await expect(secondRow).toBeVisible({ timeout: 60_000 });
    await expect(secondRow.locator('[data-project-template-documents]')).toHaveText('0');
    await expect(secondRow.getByTestId('project-template-primary')).toHaveCount(0);
    // Приєднаний без документів — від'єднати можна.
    await expect(secondRow.getByRole('button', { name: `Detach ${second.code}` })).toHaveAttribute('aria-disabled', 'false');
  });

  test('B74: new document wizard creates a document by the second template and the list shows the template', async ({ page }) => {
    await signIn(page);
    const stamp = String(Date.now());
    const primary = await publishedTemplate(page, `E2EB74G${stamp}`, 'General sheet');
    const second = await publishedTemplate(page, `E2EB74L${stamp}`, 'Land plots sheet');
    const projectCode = `E2EB74Prj${stamp}`;
    const projectId = await projectWith(page, projectCode, primary);
    // Другий шаблон — через API: вкладку «Templates» перевіряє попередній тест.
    await ok(
      await page.request.post(`/api/v1/projects/${String(projectId)}/templates`, { data: { templateVersionId: second.versionId } }),
      'приєднання другого шаблону',
    );

    await page.goto(`/?projectId=${String(projectId)}`);
    await page.getByRole('button', { name: /^New document$/ }).click();
    const dialog = page.getByRole('dialog', { name: /New document/ });
    await dialog.getByRole('textbox', { name: /^Project/ }).click();
    await page.getByRole('listbox', { name: /Project/ }).getByRole('option', { name: projectCode, exact: true }).click();
    await dialog.getByRole('button', { name: /^Next$/ }).click();

    // Крок Template: рівно два шаблони проєкту, наперед нічого не обрано.
    const templateField = dialog.getByRole('textbox', { name: /^Template/ });
    await expect(templateField).toBeVisible({ timeout: 60_000 });
    await expect(templateField).toHaveValue('');
    await expect(dialog.getByRole('button', { name: /^Next$/ })).toBeDisabled();
    await templateField.click();
    const options = page.getByRole('listbox', { name: /Template/ }).getByRole('option');
    await expect(options).toHaveCount(2);
    await options.filter({ hasText: second.code }).click();
    await expect(dialog.getByText(`${second.code} · v1.0.0.0`)).toBeVisible({ timeout: 60_000 });
    await dialog.getByRole('button', { name: /^Next$/ }).click();

    // Аркуші — другого шаблону.
    await expect(dialog.getByRole('checkbox', { name: new RegExp(second.sheetName) })).toBeVisible({ timeout: 60_000 });
    await expect(dialog.getByRole('checkbox', { name: new RegExp(primary.sheetName) })).toHaveCount(0);
    await dialog.getByRole('button', { name: /^Next$/ }).click();

    // ✎ B74 P4: огляд — шаблон під підписом «Template», версія — окремим рядком (підпис каталогу documents.version —
    // en «Template version»).
    const summary = dialog.getByTestId('wizard-summary');
    await expect(summary.getByText('Template', { exact: true })).toBeVisible();
    await expect(summary.getByText(second.code, { exact: true })).toBeVisible();
    await expect(summary.getByText('Template version', { exact: true })).toBeVisible();

    const created = page.waitForRequest((request) => request.method() === 'POST' && /\/api\/v1\/documents$/.test(request.url()));
    await dialog.getByRole('button', { name: /^Create document$/ }).click();
    const body = (await created).postDataJSON() as Record<string, unknown>;
    expect(body['templateId']).toBe(second.templateId);
    await expect(page).toHaveURL(/\/documents\/\d+/, { timeout: 60_000 });
    const documentId = /\/documents\/(\d+)/.exec(page.url())?.[1] ?? '';

    // Бізнес-ключ несе код шаблону; картка — `templateCode` другого шаблону.
    const card = await ok<{ businessKey: string; templateCode?: string | null }>(
      await page.request.get(`/api/v1/documents/${documentId}`),
      'картка документа',
    );
    expect(card.businessKey.toUpperCase()).toContain(second.code.toUpperCase());
    expect(card.templateCode).toBe(second.code);

    // Перелік: колонка «Template» рядка — код другого шаблону.
    await page.goto(`/?projectId=${String(projectId)}`);
    const row = page.locator(`[data-document-row="${card.businessKey}"]`);
    await expect(row).toBeVisible({ timeout: 60_000 });
    await expect(row.locator('[data-column="template"]')).toHaveText(second.code);
  });
});
