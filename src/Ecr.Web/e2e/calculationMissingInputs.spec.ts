import { expect, test, type APIRequestContext, type APIResponse, type Browser, type Page } from '@playwright/test';
import { signIn } from './multiSheet';

/**
 * AN-164/AN-180 на живому стенді (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep "AN-180"`): рядок, який правило
 * методології ЗАКРИЛО, але формулі бракує входу (порожня колонка `A`), раніше давав тихий нуль — 0 результатів, 0
 * невдалих кроків, 0 проблем (живий doc 96, LTD-A41). Тепер після перерахунку:
 *   (а) сервер віддає проблему `MissingInputs` з правилом, що закрило рядок, і колонкою, яку треба заповнити;
 *   (б) банер над сіткою — окрема група «… — 1 row: input data missing», підказка «формулам бракує вихідних даних»,
 *       рядок «Row 2 · rule ONLY_GAS: fill «A»»; клік відкриває таблицю й підсвічує комірку рядка.
 *
 * Фікстура — повністю через публічний API (прийом `calculationIssues.spec.ts`): шаблон з аркушем S1 і таблицею TCALC
 * (A Decimal, KIND String, OUT Calculated) -> проєкт -> документ -> два рядки «Gas» (A = 4 і A порожня) -> методологія з
 * правилом `{KIND: "Gas"}`, формулою `@A * 2`, золотим тестом і прив'язкою до OUT -> публікація -> перерахунок.
 *
 * ⚠ Дані одноразові: методологію й версію після публікації API не видаляє.
 * ⛔ Без стенда файл ПАДАЄ (вхід/запити до API), а не пропускається (honesty-guard).
 */
test.describe.configure({ timeout: 300_000 });

const TableCalc = 'TCALC';

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

function column(ordinal: number, header: string, dataType: string): Record<string, unknown> {
  return {
    headerL10n: { en: header },
    ordinal,
    dataType,
    isRequired: false,
    isReadOnly: false,
    isHidden: false,
    precision: null,
    scale: null,
    defaultValue: null,
    displayFormat: null,
    lookupFilter: null,
    lookupRegistryDefId: null,
    styleId: null,
    unitId: null,
  };
}

/** ⚠ Роль стенда `E2EAdmin` не має `Calculation.EditRule` — свій тимчасовий методолог (як у `calculationIssues.spec.ts`). */
const Stamp = Date.now().toString(36).toUpperCase().slice(-6);
const EditorRole = `E2EMI${Stamp}`;
const Editor = {
  userName: `e2e-mi-${Stamp.toLowerCase()}`,
  issued: 'E2E-Meth0d-2026!',
  password: 'E2E-Meth0d-Work-2026!',
};
const EditorPermissions = ['Calculation.View', 'Calculation.EditFormula', 'Calculation.EditRule', 'Calculation.Publish'];

async function editorContext(browser: Browser, password: string): Promise<{ request: APIRequestContext; close: () => Promise<void> }> {
  const context = await browser.newContext();
  const login = await context.request.post('/api/v1/login/local', { data: { userName: Editor.userName, password } });
  expect(login.ok(), `вхід методолога: ${String(login.status())} ${await login.text()}`).toBe(true);

  return { request: context.request, close: () => context.close() };
}

async function createEditor(page: Page, browser: Browser): Promise<{ roleId: number; userId: number }> {
  const role = await ok<{ roleId: number }>(
    await page.request.post('/api/v1/roles', {
      data: { code: EditorRole, nameL10n: { en: 'E2E missing inputs editor' }, permissionCodes: EditorPermissions },
    }),
    'роль методолога',
  );
  const user = await ok<{ userId: number }>(
    await page.request.post('/api/v1/users', {
      data: {
        userName: Editor.userName,
        provider: 'Local',
        sid: null,
        displayName: 'E2E missing inputs editor',
        initialPassword: Editor.issued,
        roleCodes: [EditorRole],
      },
    }),
    'користувач-методолог',
  );
  const first = await editorContext(browser, Editor.issued);
  try {
    await ok(
      await first.request.post('/api/v1/auth/change-password', { data: { currentPassword: Editor.issued, newPassword: Editor.password } }),
      'пароль методолога',
    );
  } finally {
    await first.close();
  }

  return { roleId: role.roleId, userId: user.userId };
}

interface IssuesResponse {
  items: {
    tableCode: string;
    rowNumber: number;
    rowKey: string;
    kind: string;
    methodologyCode: string | null;
    ruleCode?: string | null;
    columns?: { code: string; nameL10n?: { values?: Record<string, string> | null } }[] | null;
  }[];
}

interface Arranged {
  documentId: number;
  periodKey: number;
  methodologyCode: string;
  emptyRowKey: string;
}

/** Шаблон, документ, два рядки «Gas» (другий без A), опублікована методологія з правилом і прив'язкою. */
async function arrange(page: Page, editor: APIRequestContext): Promise<Arranged> {
  const stamp = String(Date.now());

  // 1. Шаблон: аркуш S1 з таблицею TCALC.
  const template = await ok<{ templateId: number }>(
    await page.request.post('/api/v1/templates', { data: { code: `E2EMi${stamp}`, nameL10n: { en: `E2E missing inputs ${stamp}` } } }),
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
  const calc = await ok<{ id: number }>(
    await page.request.put(`${base}/sheets/S1/tables/${TableCalc}`, {
      data: { nameL10n: { en: 'Calculated table' }, ordinal: 0, layoutKind: 'PerPeriodInstance', rowMode: 'Dynamic', maxDynamicRows: null },
    }),
    'таблиця з методологією',
  );
  for (const [index, [code, dataType]] of (
    [
      ['A', 'Decimal'],
      ['KIND', 'String'],
      // ⛔ Приймач виходу методології — тип Calculated (ECR-TMPL-4227).
      ['OUT', 'Calculated'],
    ] as const
  ).entries()) {
    await ok(
      await page.request.put(`${base}/tables/${String(calc.id)}/columns/${code}`, { data: column(index, code, dataType) }),
      `колонка ${code}`,
    );
  }
  const draft = await ok<{ sheets: { tables: { id: number; columns: { id: number; code: string }[] }[] }[] }>(
    await page.request.get(`${base}/structure`),
    'структура чернетки',
  );
  const calcColumns = draft.sheets[0]?.tables.find((table) => table.id === calc.id)?.columns ?? [];
  const columnId = (code: string): number => {
    const id = calcColumns.find((candidate) => candidate.code === code)?.id;
    expect(id, `колонка ${code} у структурі`).toBeDefined();

    return id ?? 0;
  };
  await ok(await page.request.post(`${base}/publish`, { data: { reason: 'e2e: AN-180 рядок без входів' } }), 'публікація шаблону');

  // 2. Проєкт і документ.
  const policies = await ok<{ id: number; code: string }[]>(await page.request.get('/api/v1/projects/period-policies'), 'політики періодів');
  const policy = policies.find((item) => item.code === 'ECR-Standard') ?? policies[0];
  expect(policy, 'у стенда немає жодної політики періодів').toBeDefined();
  const project = await ok<{ projectId: number }>(
    await page.request.post('/api/v1/projects', {
      data: {
        code: `E2EMiPrj${stamp}`,
        nameL10n: { en: `E2E missing inputs ${stamp}` },
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
  const document = await ok<{ documentId: number }>(
    await page.request.post('/api/v1/documents', {
      data: { projectId: project.projectId, sheetDefIds: structure.sheets.map((sheet) => sheet.id), templateVersionId: version.versionId },
    }),
    'документ',
  );
  const calendar = await ok<{ periods: { periodKey: number; state: string }[] }>(
    await page.request.get(`/api/v1/projects/${String(project.projectId)}/periods`),
    'календар проєкту',
  );
  const periodKey = (calendar.periods.find((item) => item.state === 'Open') ?? calendar.periods[0])?.periodKey ?? 0;
  expect(periodKey, 'у проєкту немає періодів').toBeGreaterThan(0);

  const documentUrl = `/api/v1/documents/${String(document.documentId)}`;
  const tables = await ok<{ tableInstanceId: number; tableCode: string }[]>(
    await page.request.get(`${documentUrl}/tables?periodKey=${String(periodKey)}`),
    'таблиці документа',
  );
  const tableInstanceId = tables.find((table) => table.tableCode === TableCalc)?.tableInstanceId ?? 0;
  expect(tableInstanceId, `екземпляр таблиці ${TableCalc}`).toBeGreaterThan(0);

  // 3. Два рядки «Gas»: перший з A = 4 (порахується), другий без A (правило закриє, входу немає).
  for (const n of [1, 2]) {
    await ok(await page.request.post(`${documentUrl}/rows`, { data: { tableInstanceId, rowKey: null } }), `рядок ${String(n)}`);
  }
  const slice = await ok<{ rows: { rowKey: string; rowVersion: string }[] }>(
    await page.request.get(`${documentUrl}/tables/${String(tableInstanceId)}`),
    'зріз таблиці',
  );
  expect(slice.rows, 'у таблиці мають бути два рядки').toHaveLength(2);
  const [filled, empty] = slice.rows;
  expect(filled).toBeDefined();
  expect(empty).toBeDefined();
  await ok(
    await page.request.patch(`${documentUrl}/cells`, {
      data: {
        tableInstanceId,
        periodKey,
        origin: 'UserEdit',
        rows: [
          { rowKey: filled?.rowKey, baseVersion: filled?.rowVersion, cells: [{ columnCode: 'KIND', value: 'Gas', isEmpty: false }, { columnCode: 'A', value: '4', isEmpty: false }] },
          { rowKey: empty?.rowKey, baseVersion: empty?.rowVersion, cells: [{ columnCode: 'KIND', value: 'Gas', isEmpty: false }] },
        ],
      },
    }),
    'значення рядків',
  );

  // 4. Методологія: вихід, формула `@A * 2`, правило «KIND = Gas», золотий тест, прив'язка до OUT, публікація.
  const units = await ok<{ id: number; code: string }[]>(await editor.get('/api/v1/units'), 'одиниці');
  const unitId = (units.find((unit) => unit.code === 't') ?? units[0])?.id ?? 0;
  const methodologyCode = `E2EMI_${stamp}`;
  const methodology = await ok<{ id: number }>(
    await editor.post('/api/v1/methodologies', {
      data: { code: methodologyCode, group: null, kind: 'DataDriven', nameL10n: { en: `E2E missing inputs ${stamp}` } },
    }),
    'методологія',
  );
  const draftVersion = await ok<{ id: number }>(
    await editor.post(`/api/v1/methodologies/${String(methodology.id)}/versions`, {
      data: { versionNumber: '1.0', copyFromVersionId: null, level: 'Configuration' },
    }),
    'чернетка методології',
  );
  const mBase = `/api/v1/methodologies/${String(methodology.id)}`;
  const vBase = `${mBase}/versions/${String(draftVersion.id)}`;
  await ok(await editor.put(`${vBase}/outputs/EMISSION`, { data: { unitId, ordinal: 1 } }), 'вихід EMISSION');
  await ok(
    await editor.put(`${vBase}/formulas/EMISSION`, {
      data: { expression: '@A * 2', resultType: 'Number', outputUnitId: unitId, argumentsCsv: 'A' },
    }),
    'формула EMISSION',
  );
  await ok(
    await editor.put(`${vBase}/rules/ONLY_GAS`, {
      data: { matchJson: JSON.stringify({ [String(columnId('KIND'))]: 'Gas' }), priority: 1, isActive: true },
    }),
    'правило ONLY_GAS',
  );
  await ok(
    await editor.put(`${vBase}/tests/GOLDEN`, {
      data: {
        inputJson: JSON.stringify({
          documentId: document.documentId,
          tableInstanceId,
          periodKey: { value: periodKey },
          sourceRowKey: filled?.rowKey,
          arguments: [{ argumentCode: 'A', value: 4, valueString: null, unitId: null }],
        }),
        expectedJson: '{"EMISSION":8}',
        tolerance: 0.0001,
      },
    }),
    'золотий тест',
  );
  await ok(
    await editor.put(`${mBase}/bindings/${String(columnId('OUT'))}/EMISSION`, { data: { matchJson: '{}', isActive: true } }),
    'прив’язка до OUT',
  );
  // ⛔ Дата чинності — раніше за період: версію добирають за датою ПЕРІОДУ.
  await ok(
    await editor.post(`${vBase}/publish`, {
      data: { changeReason: 'e2e: AN-180 рядок без входів', effectiveFrom: `${String(Math.floor(periodKey / 100) - 1)}-01-01` },
    }),
    'публікація методології',
  );

  return { documentId: document.documentId, periodKey, methodologyCode, emptyRowKey: empty?.rowKey ?? '' };
}

/** Ставить перерахунок документа в чергу і чекає кінця задачі (Succeeded). */
async function recalculate(page: Page, arranged: Arranged): Promise<void> {
  const accepted = await ok<{ jobId: string }>(
    await page.request.post(`/api/v1/documents/${String(arranged.documentId)}/recalculate`, { data: { periodKey: arranged.periodKey } }),
    'перерахунок',
  );
  await expect
    .poll(
      async () => {
        const response = await page.request.get(`/api/v1/jobs/${encodeURIComponent(accepted.jobId)}`);
        if (!response.ok()) return `HTTP ${String(response.status())}`;

        return ((await response.json()) as { state?: string }).state ?? 'unknown';
      },
      { timeout: 120_000, intervals: [500, 1_000, 2_000], message: 'задача перерахунку не дійшла до Succeeded' },
    )
    .toBe('Succeeded');
}

test('AN-180 missing inputs (бракує входів формул): a matched row without formula inputs shows the «input data missing» group with the column and jumps to the cell', async ({ page, browser }) => {
  await signIn(page);
  await page.setViewportSize({ width: 1440, height: 900 });
  const created = { roleId: 0, userId: 0 };
  try {
    await runScenario(page, browser, created);
  } finally {
    // Користувач лишається без ролей (видалити його API не дає); роль видаляється ПІСЛЯ зняття (інакше 409).
    if (created.userId !== 0) await page.request.put(`/api/v1/users/${String(created.userId)}/roles`, { data: { roleCodes: [] } });
    if (created.roleId !== 0) await page.request.delete(`/api/v1/roles/${String(created.roleId)}`);
  }
});

async function runScenario(page: Page, browser: Browser, created: { roleId: number; userId: number }): Promise<void> {
  const ids = await createEditor(page, browser);
  created.roleId = ids.roleId;
  created.userId = ids.userId;
  const editor = await editorContext(browser, Editor.password);
  let arranged: Arranged;
  try {
    arranged = await arrange(page, editor.request);
  } finally {
    await editor.close();
  }
  await recalculate(page, arranged);

  // 1. Сервер: рівно одна проблема — рядок № 2 без A, вид MissingInputs, правило ONLY_GAS, колонка A.
  const issuesUrl = `/api/v1/documents/${String(arranged.documentId)}/calculation-issues?periodKey=${String(arranged.periodKey)}`;
  await expect
    .poll(async () => (await ok<IssuesResponse>(await page.request.get(issuesUrl), 'проблеми розрахунку')).items.length, {
      timeout: 60_000,
      message: 'сервер не записав проблему MissingInputs після перерахунку (тихий нуль)',
    })
    .toBe(1);
  const issues = await ok<IssuesResponse>(await page.request.get(issuesUrl), 'проблеми розрахунку');
  expect(issues.items[0]).toMatchObject({
    tableCode: TableCalc,
    rowNumber: 2,
    rowKey: arranged.emptyRowKey,
    kind: 'MissingInputs',
    methodologyCode: arranged.methodologyCode,
    ruleCode: 'ONLY_GAS',
  });
  expect((issues.items[0]?.columns ?? []).map((c) => c.code)).toEqual(['A']);
  // AN-164b: колонка таблиці приходить ІЗ заголовком (порожній заголовок означає «вхід поза колонками» — інший підпис).
  expect(issues.items[0]?.columns?.[0]?.nameL10n?.values?.en).toBe('A');

  // 2. Клієнт: банер (згорнутий за замовчуванням — пам'ять згортання чиста) → розгорнути.
  await page.goto(`/documents/${String(arranged.documentId)}?periodKey=${String(arranged.periodKey)}`);
  await page.evaluate(() => {
    try {
      for (const key of Object.keys(window.localStorage)) {
        if (key.startsWith('ecr.unmatchedRows.collapsed')) window.localStorage.removeItem(key);
      }
    } catch {
      // Сховище недоступне — банер однаково згорнутий за замовчуванням.
    }
  });
  await page.reload();
  // ✎ B78: одна панель «Проблеми розрахунку» — причина «немає входних даних» рядком з бейджем; перелік — у деталях.
  const panel = page.getByTestId('document-calc-issues');
  await expect(panel).toBeVisible({ timeout: 60_000 });
  const cause = page.getByTestId('document-calc-issue');
  await expect(cause).toHaveCount(1);
  await expect(cause).toHaveAttribute('data-kind', 'missingInputs');
  await expect(page.getByTestId('document-calc-issue-title')).toContainText('1 row not calculated: input data are missing');
  const toggle = page.getByTestId('document-calc-issues-details-toggle');
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-expanded', 'true');

  const banner = page.getByTestId('document-unmatched-rows');
  const group = banner.getByTestId('document-unmatched-rows-group');
  await expect(group).toHaveCount(1);
  await expect(group).toContainText('Sheet 1 · Calculated table — 1 row: input data missing');
  await expect(page.getByTestId('document-missing-input-rows-hint')).toContainText('the formulas lack input data');
  // Підказки «без правила» і «категорію не визначено» тут неправдиві — їх немає.
  await expect(page.getByTestId('document-unmatched-rows-hint')).toHaveCount(0);
  await expect(page.getByTestId('document-rejected-rows-hint')).toHaveCount(0);

  // 3. Рядок — правило й колонка до заповнення; клік відкриває таблицю й підсвічує комірку рядка.
  const link = group.getByTestId('document-rejected-row-link');
  await expect(link).toHaveText('Row 2 · rule ONLY_GAS: fill «A»');
  await expect(link).toHaveAttribute('data-row-key', arranged.emptyRowKey);
  await link.click();
  const calcItem = page.locator(`[role="treeitem"][data-tree-table]`, { hasText: 'Calculated table' });
  await expect(calcItem).toHaveAttribute('aria-selected', 'true', { timeout: 30_000 });
  await expect(page.locator('.ecr-cell-nav-target'), 'підсвічена комірка рядка').toHaveCount(1, { timeout: 30_000 });
}
