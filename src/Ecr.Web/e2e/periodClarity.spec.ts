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

/**
 * Pass2 (людський прохід 07.11, P3, пп. 4–7) — `-Grep "Pass2:"`. Проєкти й документи власні, як і вище.
 */
test.describe('Pass2 (людський прохід 07.11)', () => {
  test.describe.configure({ mode: 'serial', timeout: 300_000 });

  /** Майстер «New document» на обраному проєкті, до кроку Project з обраним проєктом. */
  async function wizardOnProject(page: Page, project: Project) {
    await page.goto(`/?projectId=${String(project.id)}`);
    await page.getByRole('button', { name: /^New document$/ }).click();
    const dialog = page.getByRole('dialog', { name: /New document/ });
    await dialog.getByRole('textbox', { name: /^Project/ }).click();
    await page.getByRole('listbox', { name: /Project/ }).getByRole('option', { name: project.code, exact: true }).click();

    return dialog;
  }

  test('Pass2: new document wizard warns that the project already has a document and opens it instead', async ({ page }) => {
    await signIn(page);
    const { id, project, card } = await partlySubmittedDocument(page);
    let posts = 0;
    page.on('request', (request) => {
      if (request.method() === 'POST' && /\/api\/v1\/documents$/.test(request.url())) posts += 1;
    });

    const dialog = await wizardOnProject(page, project);
    const banner = dialog.getByTestId('create-existing-documents');
    await expect(banner).toBeVisible({ timeout: 60_000 });
    await expect(banner).toContainText('This project already has a document');
    await expect(banner).toContainText(card.businessKey);

    // «Створити ще один» лишається: Next доступний.
    await expect(dialog.getByRole('button', { name: /^Next$/ })).toBeEnabled();

    await banner.getByRole('button', { name: `Open ${card.businessKey}` }).click();
    await expect(page).toHaveURL(new RegExp(`/documents/${String(id)}(\\?|$)`), { timeout: 60_000 });
    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(posts, 'документ не мав створюватися').toBe(0);
  });

  test('Pass2: Esc in the new document wizard asks to discard what was entered', async ({ page }) => {
    await signIn(page);
    const { project } = await partlySubmittedDocument(page);

    const dialog = await wizardOnProject(page, project);
    await expect(dialog.getByRole('button', { name: /^Next$/ })).toBeEnabled({ timeout: 60_000 });

    await page.keyboard.press('Escape');
    const confirm = page.getByRole('dialog', { name: 'Discard what you entered?' });
    await expect(confirm).toBeVisible({ timeout: 10_000 });

    // «Keep editing» — майстер лишається з обраним проєктом.
    await confirm.getByRole('button', { name: 'Keep editing' }).click();
    await expect(confirm).toBeHidden();
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole('textbox', { name: /^Project/ })).toHaveValue(project.code);

    // ✕ у шапці — те саме питання; «Discard» закриває майстер.
    await dialog.locator('.mantine-Modal-close').click();
    await expect(confirm).toBeVisible({ timeout: 10_000 });
    await confirm.getByRole('button', { name: 'Discard' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0, { timeout: 10_000 });

    // Відкрито знову — порожній майстер, і Esc закриває його одразу, без питання.
    await page.getByRole('button', { name: /^New document$/ }).click();
    await expect(dialog.getByRole('textbox', { name: /^Project/ })).toHaveValue('');
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0, { timeout: 10_000 });
  });

  test('Pass2: periods of a draft project do not promise past opening dates and the counters say all projects', async ({ page }) => {
    await signIn(page);
    // Власний проєкт-ЧЕРНЕТКА: шаблон з одним аркушем, проєкт без активації.
    const stamp = String(Date.now());
    const template = await ok<{ templateId: number }>(
      await page.request.post('/api/v1/templates', { data: { code: `E2EPass2Tpl${stamp}`, nameL10n: { en: `E2E pass2 ${stamp}` } } }),
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
        data: { nameL10n: { en: 'Sheet 1' }, ordinal: 1, isMandatory: true, isVisible: true, sheetGroup: null },
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
    await ok(await page.request.post(`${base}/publish`, { data: { reason: 'e2e: Pass2 draft project periods' } }), 'публікація');
    const policies = await ok<{ id: number; code: string }[]>(await page.request.get('/api/v1/projects/period-policies'), 'політики');
    const policy = policies.find((item) => item.code === 'ECR-Standard') ?? policies[0];
    const created = await ok<{ projectId: number }>(
      await page.request.post('/api/v1/projects', {
        data: {
          code: `E2EPass2Draft${stamp}`,
          nameL10n: { en: `E2E pass2 draft ${stamp}` },
          periodKind: 'Monthly',
          timeZoneId: 'UTC',
          templateVersionId: version.versionId,
          periodPolicyId: policy?.id,
          year: new Date().getUTCFullYear(),
        },
      }),
      'проєкт-чернетка',
    );

    await page.goto(`/admin/periods?projectId=${String(created.projectId)}`);
    const tiles = page.locator('[data-testid="periods-year"] [data-period-tile]');
    await expect(tiles.first()).toBeVisible({ timeout: 60_000 });
    const count = await tiles.count();
    expect(count, 'у чернетки немає плиток місяців').toBeGreaterThan(0);
    let scheduled = 0;
    for (let i = 0; i < count; i += 1) {
      const tile = tiles.nth(i);
      if ((await tile.getAttribute('data-state')) !== 'Scheduled') continue;
      scheduled += 1;
      await expect(tile).toContainText('not open — the project is a draft');
      await expect(tile).not.toContainText(/opens /);
    }
    expect(scheduled, 'у чернетки немає жодного ще не відкритого місяця — перевіряти нічого').toBeGreaterThan(0);

    // Смуга показників видимо каже, що рахує всі проєкти.
    await expect(page.locator('[data-periods-stats-scope="all"]')).toContainText('All projects:');
  });

  test('Pass2: documents year view subtitle speaks about the selected year, not one period', async ({ page }) => {
    await signIn(page);
    await page.goto('/');
    const subtitle = page.getByTestId('page-description');
    await expect(subtitle).toBeVisible({ timeout: 60_000 });
    await expect(subtitle).toHaveText('Reports of your projects for the selected year. Open a document to work in it.');
    await expect(page.locator('[data-documents-year-note]')).toBeVisible();
  });
});
