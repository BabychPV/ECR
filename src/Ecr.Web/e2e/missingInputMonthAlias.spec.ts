import { expect, test, type APIRequestContext, type APIResponse, type Page } from '@playwright/test';

/**
 * Missing-input month alias (live doc 96, Land 1.0.20.0): на документі «рік» панель «Проблеми розрахунку» казала
 * «input data «Land_RunningHoursMonthly» (this table has no such column)», хоча це псевдонім місячного входу, і колонка
 * `…_M10` («Running hours per month (h)» під жовтнем) у таблиці Є. Сервер тепер називає колонку місяця періоду з місяцем
 * (`CalculationRejectionReasonsMonthAliasTests`), а тут — справжній стенд і справжній Chromium:
 *   (а) панель жовтня на якорі року показує «fill «<заголовок> · Oct»» — без «no such column» для цієї колонки, а справжній
 *       вхід поза колонками — з ним;
 *   (б) клік веде в СІТКУ на комірку саме колонки жовтня (підсвічена комірка під заголовком цієї колонки).
 *
 * ⚠ Документ «рік» (шаблон `YearWithMonthColumns`, таблиця `MonthsInColumns`, 12 місячних колонок) створюється через API,
 * а відповідь `calculation-issues?year=true` доповнюється в браузері проблемами `MissingInputs` у ТІЙ формі, яку дає
 * виправлений сервер: псевдонімів і прогону методології річної розкладки стенд не має. Резолюцію псевдоніма на сервері
 * тримають модульні тести `CalculationRejectionReasonsMonthAliasTests` (мутації — у них).
 * ⛔ Без стенда файл ПАДАЄ (вхід/запити до API), а не пропускається (honesty-guard).
 */
const Operator = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Header = (month: number): string => `Running hours per month (h) M${String(month).padStart(2, '0')}`;
const Code = (month: number): string => `HRS_M${String(month).padStart(2, '0')}`;

test('missing-input month alias: year document names the month column (with month), not «no such column», and jumps to it', async ({ page }) => {
  // ⚠ Фікстура — ~35 запитів (шаблон, 13 колонок, проєкт, екземпляри 12 місяців): типових 30 с не вистачає.
  test.setTimeout(300_000);
  await page.setViewportSize({ width: 1440, height: 900 });
  await signIn(page, Operator.user, Operator.password);
  const arranged = await arrangeYearDocument(page.request);

  let yearRead = false;
  await page.route(/\/api\/v1\/documents\/\d+\/calculation-issues/, async (route) => {
    const response = await route.fetch();
    const body = (await response.json()) as Record<string, unknown>;
    if (route.request().url().includes('year=true')) {
      yearRead = true;
      const missing = (monthNumber: number, columns: unknown[]) => ({
        tableCode: 'T1', sheetCopyId: null, rowNumber: 3, rowKey: 'R3', kind: 'MissingInputs', methodologyCode: 'ECW_C05_04',
        detectedAt: '2026-11-24T08:00:00Z', ruleCode: 'R_C05_04_01', monthNumber, methodologyId: null, columns,
      });
      const month = (m: number, short: string) => ({ code: Code(m), nameL10n: { values: { en: `${Header(m)} · ${short}` } } });
      body['items'] = [
        ...((body['items'] as unknown[] | undefined) ?? []),
        missing(10, [month(10, 'Oct'), { code: 'Land_Density', nameL10n: { values: {} } }]),
      ];
    }
    await route.fulfill({ response, json: body });
  });

  await page.goto(`/documents/${String(arranged.documentId)}?periodKey=${String(arranged.october)}`);
  await expect(page).toHaveURL(new RegExp(`periodKey=${String(arranged.anchor)}`), { timeout: 90_000 });

  // Проблема жовтня — у панелі жовтня: колонка жовтня й справжній вхід поза колонками.
  const panel = page.locator(`[data-testid="document-calc-issues"][data-period="${String(arranged.october)}"]`);
  await expect(panel, 'панелі проблем жовтня на якорі року немає').toBeVisible({ timeout: 60_000 });
  expect(yearRead, 'сторінка «рік» не читала проблеми року (year=true)').toBe(true);
  const cause = panel.locator('[data-testid="document-calc-issue"][data-kind="missingInputs"]');
  await expect(cause).toHaveCount(1);
  const toggle = panel.getByTestId('document-calc-issues-details-toggle');
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-expanded', 'true');

  const link = panel.getByTestId('document-rejected-row-link');
  await expect(link).toHaveCount(1);
  await expect(link).toHaveText(
    `Row 3 · rule R_C05_04_01: fill «${Header(10)} · Oct», input data «Land_Density» (this table has no such column)`,
  );
  await expect(link).not.toContainText('HRS_M10');
  await expect(link).not.toContainText('document.');

  await page.screenshot({ path: test.info().outputPath('missing-input-month-alias-panel.png'), fullPage: false });

  // Клік — комірка рядка R3 саме в колонці жовтня (центр підсвіченої комірки — під заголовком цієї колонки).
  await link.click();
  const target = page.locator('.ecr-cell-nav-target');
  await expect(target, 'підсвіченої комірки немає').toHaveCount(1, { timeout: 60_000 });
  const header = page.getByText(Header(10), { exact: true }).first();
  await expect(header).toBeInViewport({ timeout: 30_000 });
  const [cell, head] = [await target.boundingBox(), await header.boundingBox()];
  expect(cell, 'немає меж комірки').not.toBeNull();
  expect(head, 'немає меж заголовка').not.toBeNull();
  const centre = (cell?.x ?? 0) + (cell?.width ?? 0) / 2;
  expect(centre, 'підсвічена комірка не в колонці жовтня').toBeGreaterThanOrEqual((head?.x ?? 0) - 4);
  expect(centre, 'підсвічена комірка не в колонці жовтня').toBeLessThanOrEqual((head?.x ?? 0) + Math.max(head?.width ?? 0, cell?.width ?? 0) + 4);

  await page.screenshot({ path: test.info().outputPath('missing-input-month-alias-cell.png'), fullPage: false });
});

interface YearDocument {
  readonly documentId: number;
  readonly anchor: number;
  readonly october: number;
}

/** Документ «рік» як Land: місячний проєкт, шаблон `YearWithMonthColumns`, рядки R1..R5 на якорі. */
async function arrangeYearDocument(request: APIRequestContext): Promise<YearDocument> {
  const ok = async <T,>(response: APIResponse, what: string): Promise<T> => {
    const body = await response.text();
    expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1000)}`).toBeLessThan(300);
    return (body.length > 0 ? JSON.parse(body) : {}) as T;
  };
  const stamp = String(Date.now());
  const year = new Date().getUTCFullYear() - 1;

  const template = await ok<{ templateId: number }>(
    await request.post('/api/v1/templates', { data: { code: `E2EMIA${stamp}`, nameL10n: { en: `E2E month alias ${stamp}` } } }),
    'шаблон',
  );
  const version = await ok<{ versionId: number }>(
    await request.post(`/api/v1/templates/${String(template.templateId)}/versions`, { data: { versionNumber: '1.0.0.0', cloneFromVersionId: null } }),
    'версія',
  );
  const base = `/api/v1/template-versions/${String(version.versionId)}`;
  await ok(await request.put(`${base}/layout`, { data: { layout: 'YearWithMonthColumns' } }), 'розкладка «рік»');
  await ok(
    await request.put(`${base}/sheets/S1`, { data: { nameL10n: { en: 'Fuel sheet' }, ordinal: 1, isMandatory: true, isVisible: true, sheetGroup: null } }),
    'аркуш',
  );
  const table = await ok<{ id: number }>(
    await request.put(`${base}/sheets/S1/tables/T1`, {
      data: { nameL10n: { en: 'Fuel table' }, ordinal: 0, layoutKind: 'MonthsInColumns', rowMode: 'Dynamic', maxDynamicRows: 60 },
    }),
    'таблиця',
  );
  const column = (ordinal: number, header: string, dataType: 'String' | 'Decimal', month: number | null): Record<string, unknown> => ({
    headerL10n: { en: header }, ordinal, dataType, isRequired: false, isReadOnly: false, isHidden: false,
    precision: dataType === 'Decimal' ? 18 : null, scale: dataType === 'Decimal' ? 2 : null, defaultValue: null,
    displayFormat: null, lookupFilter: null, lookupRegistryDefId: null, styleId: null, unitId: null,
    ...(month === null ? {} : { isMonthColumn: true, monthNumber: month }),
  });
  await ok(await request.put(`${base}/tables/${String(table.id)}/columns/NAME`, { data: column(0, 'Name', 'String', null) }), 'стовпець NAME');
  for (let m = 1; m <= 12; m += 1) {
    await ok(await request.put(`${base}/tables/${String(table.id)}/columns/${Code(m)}`, { data: column(m, Header(m), 'Decimal', m) }), `стовпець ${Code(m)}`);
  }
  await ok(await request.post(`${base}/publish`, { data: { reason: 'e2e: missing-input month alias' } }), 'публікація');

  const policy = await ok<{ id: number }>(
    await request.post('/api/v1/projects/period-policies', {
      data: { code: `E2EMIA${stamp}`, openOffsetDays: 0, graceOffsetDays: 3000, hardCloseOffsetDays: 3600, yearGraceOffsetDays: 3600 },
    }),
    'політика періодів',
  );
  const project = await ok<{ projectId: number }>(
    await request.post('/api/v1/projects', {
      data: {
        code: `E2EMIAPrj${stamp}`, nameL10n: { en: `E2E month alias ${stamp}` }, periodKind: 'Monthly', timeZoneId: 'UTC',
        templateVersionId: version.versionId, periodPolicyId: policy.id, year,
      },
    }),
    'проєкт',
  );
  await ok(await request.post(`/api/v1/projects/${String(project.projectId)}/activate`), 'активація');
  const structure = await ok<{ sheets: { id: number }[] }>(await request.get(`${base}/structure`), 'структура');
  const document = await ok<{ documentId: number }>(
    await request.post('/api/v1/documents', {
      data: { projectId: project.projectId, sheetDefIds: structure.sheets.map((s) => s.id), templateVersionId: version.versionId },
    }),
    'документ',
  );

  // Екземпляри всіх місяців (власники місячних колонок) і рядки R1..R5 на якорі.
  let anchorTables: { tableInstanceId: number; tableCode: string }[] = [];
  for (let m = 1; m <= 12; m += 1) {
    const rows = await ok<{ tableInstanceId: number; tableCode: string }[]>(
      await request.get(`/api/v1/documents/${String(document.documentId)}/tables?periodKey=${String(year * 100 + m)}`),
      `таблиці ${String(m)}`,
    );
    if (m === 1) anchorTables = rows;
  }
  const t1 = anchorTables.find((t) => t.tableCode === 'T1');
  if (t1 === undefined) throw new Error('у якоря немає таблиці T1');
  for (let r = 1; r <= 5; r += 1) {
    await ok(
      await request.post(`/api/v1/documents/${String(document.documentId)}/rows`, { data: { tableInstanceId: t1.tableInstanceId, rowKey: `R${String(r)}` } }),
      `рядок R${String(r)}`,
    );
  }

  return { documentId: document.documentId, anchor: year * 100 + 1, october: year * 100 + 10 };
}

async function signIn(page: Page, user: string, password: string): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}
