import { expect, test, type APIRequestContext, type APIResponse, type Browser, type Page } from '@playwright/test';
import { signIn } from './multiSheet';

/**
 * AN-69 на живому стенді (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep "calculation issues"`):
 * після перерахунку рядок, якого не охопило жодне правило методології, дає
 *   (а) жовту позначку з кількістю на таблиці в дереві таблиць аркуша (`.ecr-tnav-norule`, `aria-label` з
 *       ключа `calculationIssues.badgeLabel`);
 *   (б) згортну панель на сторінці документа зі списком рядків; клік по рядку вибирає таблицю (`?table=`).
 *
 * Фікстура — повністю через публічний API (сід методологій не містить):
 *   шаблон з аркушем S1 і двома таблицями (TPLAIN — без методології, TCALC — A Decimal, KIND String, OUT Calculated) ->
 *   проєкт -> документ -> два рядки TCALC (KIND = Gas, KIND = Coal) -> методологія з правилом `{KIND: "Gas"}`,
 *   виходом EMISSION, формулою `@A * 2`, золотим тестом і прив'язкою до OUT -> публікація -> перерахунок документа.
 * Рядок «Coal» (№ 2) правилу не підходить -> проблема `NoMatchingRule`.
 *
 * Спершу перевіряється ВІДПОВІДЬ СЕРВЕРА (`GET /documents/{id}/calculation-issues`), потім клієнт: червоний тест
 * одразу каже, де зламано.
 *
 * ⚠ Дані одноразові (як `sheetCopies.spec.ts`): методологію й версію після публікації API не видаляє.
 * ⛔ Без стенда файл ПАДАЄ (вхід/запити до API), а не пропускається (honesty-guard).
 */
test.describe.configure({ timeout: 300_000 });

const TableCalc = 'TCALC';
const TablePlain = 'TPLAIN';

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

/**
 * ⚠ Роль стенда `E2EAdmin` не має `Calculation.EditRule` (правило відбору рядків, прив'язка), а роль стенда тут не
 * змінюється. Тому — свій методолог (прийом `methodologyCategories.spec.ts`): тимчасова роль із правами методології
 * і користувач із нею; прибирання — у `finally` тесту (роль знімається з користувача, потім видаляється).
 */
const Stamp = Date.now().toString(36).toUpperCase().slice(-6);
const EditorRole = `E2ECI${Stamp}`;
const Editor = {
  userName: `e2e-ci-${Stamp.toLowerCase()}`,
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

/** Роль і користувач-методолог; пароль одразу змінюється з разового на робочий. Повертає їхні ідентифікатори. */
async function createEditor(page: Page, browser: Browser): Promise<{ roleId: number; userId: number }> {
  const role = await ok<{ roleId: number }>(
    await page.request.post('/api/v1/roles', {
      data: { code: EditorRole, nameL10n: { en: 'E2E calc issues editor' }, permissionCodes: EditorPermissions },
    }),
    'роль методолога',
  );
  const user = await ok<{ userId: number }>(
    await page.request.post('/api/v1/users', {
      data: {
        userName: Editor.userName,
        provider: 'Local',
        sid: null,
        displayName: 'E2E calc issues editor',
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
  items: { tableCode: string; rowNumber: number; rowKey: string; kind: string; methodologyCode: string | null }[];
}

interface Arranged {
  documentId: number;
  periodKey: number;
  methodologyCode: string;
  coalRowKey: string;
}

/** Шаблон, документ, два рядки, опублікована методологія з правилом і прив'язкою. Перерахунок — окремо. */
async function arrange(page: Page, editor: APIRequestContext): Promise<Arranged> {
  const stamp = String(Date.now());

  // 1. Шаблон: аркуш S1 з двома таблицями.
  const template = await ok<{ templateId: number }>(
    await page.request.post('/api/v1/templates', { data: { code: `E2ECi${stamp}`, nameL10n: { en: `E2E calc issues ${stamp}` } } }),
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
  const plain = await ok<{ id: number }>(
    await page.request.put(`${base}/sheets/S1/tables/${TablePlain}`, {
      data: { nameL10n: { en: 'Plain table' }, ordinal: 0, layoutKind: 'PerPeriodInstance', rowMode: 'Dynamic', maxDynamicRows: null },
    }),
    'таблиця без методології',
  );
  await ok(await page.request.put(`${base}/tables/${String(plain.id)}/columns/N`, { data: column(0, 'N', 'String') }), 'колонка N');
  const calc = await ok<{ id: number }>(
    await page.request.put(`${base}/sheets/S1/tables/${TableCalc}`, {
      data: { nameL10n: { en: 'Calculated table' }, ordinal: 1, layoutKind: 'PerPeriodInstance', rowMode: 'Dynamic', maxDynamicRows: null },
    }),
    'таблиця з методологією',
  );
  for (const [index, [code, dataType]] of (
    [
      ['A', 'Decimal'],
      ['KIND', 'String'],
      // ⛔ Приймач виходу методології — тип Calculated (ECR-TMPL-4227 не дає прив'язати вихід до колонки ручного вводу).
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
  await ok(await page.request.post(`${base}/publish`, { data: { reason: 'e2e: AN-69 проблеми розрахунку' } }), 'публікація шаблону');

  // 2. Проєкт і документ.
  const policies = await ok<{ id: number; code: string }[]>(await page.request.get('/api/v1/projects/period-policies'), 'політики періодів');
  const policy = policies.find((item) => item.code === 'ECR-Standard') ?? policies[0];
  expect(policy, 'у стенда немає жодної політики періодів').toBeDefined();
  const project = await ok<{ projectId: number }>(
    await page.request.post('/api/v1/projects', {
      data: {
        code: `E2ECiPrj${stamp}`,
        nameL10n: { en: `E2E calc issues ${stamp}` },
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

  // 3. Два рядки: Gas (закриє правило) і Coal (правило не закриє).
  for (const n of [1, 2]) {
    await ok(
      await page.request.post(`${documentUrl}/rows`, { data: { tableInstanceId, rowKey: null } }),
      `рядок ${String(n)}`,
    );
  }
  const slice = await ok<{ rows: { rowKey: string; rowVersion: string }[] }>(
    await page.request.get(`${documentUrl}/tables/${String(tableInstanceId)}`),
    'зріз таблиці',
  );
  expect(slice.rows, 'у таблиці мають бути два рядки').toHaveLength(2);
  const [gas, coal] = slice.rows;
  expect(gas).toBeDefined();
  expect(coal).toBeDefined();
  await ok(
    await page.request.patch(`${documentUrl}/cells`, {
      data: {
        tableInstanceId,
        periodKey,
        origin: 'UserEdit',
        rows: [
          { rowKey: gas?.rowKey, baseVersion: gas?.rowVersion, cells: [{ columnCode: 'KIND', value: 'Gas', isEmpty: false }, { columnCode: 'A', value: '4', isEmpty: false }] },
          { rowKey: coal?.rowKey, baseVersion: coal?.rowVersion, cells: [{ columnCode: 'KIND', value: 'Coal', isEmpty: false }, { columnCode: 'A', value: '5', isEmpty: false }] },
        ],
      },
    }),
    'значення рядків',
  );

  // 4. Методологія: вихід, формула, правило «KIND = Gas», золотий тест, прив'язка до OUT, публікація.
  const units = await ok<{ id: number; code: string }[]>(await editor.get('/api/v1/units'), 'одиниці');
  const unitId = (units.find((unit) => unit.code === 't') ?? units[0])?.id ?? 0;
  const methodologyCode = `E2ECI_${stamp}`;
  const methodology = await ok<{ id: number }>(
    await editor.post('/api/v1/methodologies', {
      data: { code: methodologyCode, group: null, kind: 'DataDriven', nameL10n: { en: `E2E calc issues ${stamp}` } },
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
      // ⛔ Без argumentsCsv звірка списку аргументів мовчить, а золотий тест читає аргумент A.
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
          sourceRowKey: gas?.rowKey,
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
      data: { changeReason: 'e2e: AN-69 проблеми розрахунку', effectiveFrom: `${String(Math.floor(periodKey / 100) - 1)}-01-01` },
    }),
    'публікація методології',
  );

  return { documentId: document.documentId, periodKey, methodologyCode, coalRowKey: coal?.rowKey ?? '' };
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

test('AN-69 calculation issues (проблеми розрахунку): row without a matching rule shows the badge in the table tree and the panel; a click selects the table', async ({ page, browser }) => {
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

  // 1. Сервер: рівно одна проблема — рядок Coal (№ 2) таблиці TCALC; Gas правилом закрито.
  const issuesUrl = `/api/v1/documents/${String(arranged.documentId)}/calculation-issues?periodKey=${String(arranged.periodKey)}`;
  await expect
    .poll(async () => (await ok<IssuesResponse>(await page.request.get(issuesUrl), 'проблеми розрахунку')).items.length, {
      timeout: 60_000,
      message: 'сервер не записав проблему NoMatchingRule після перерахунку',
    })
    .toBe(1);
  const issues = await ok<IssuesResponse>(await page.request.get(issuesUrl), 'проблеми розрахунку');
  expect(issues.items[0]).toMatchObject({
    tableCode: TableCalc,
    rowNumber: 2,
    rowKey: arranged.coalRowKey,
    kind: 'NoMatchingRule',
    methodologyCode: arranged.methodologyCode,
  });

  // 2. Клієнт: позначка в дереві таблиць (саме таблиця з методологією, не інша).
  await page.goto(`/documents/${String(arranged.documentId)}?periodKey=${String(arranged.periodKey)}`);
  const calcItem = page.locator(`[role="treeitem"][data-tree-table]`, { hasText: 'Calculated table' });
  await expect(calcItem, 'таблиця з методологією в дереві').toBeVisible({ timeout: 60_000 });
  await expect(calcItem.getByTestId('table-no-rule-badge')).toHaveText('1');
  await expect(calcItem.locator('.ecr-tnav-norule')).toHaveCount(1);
  await expect(calcItem).toHaveAttribute('aria-label', /Rows without a calculation rule: 1/);
  await expect(page.getByTestId('table-no-rule-badge'), 'позначка лише на одній таблиці').toHaveCount(1);

  // 3. UAT 08.11 b1: банер «Some rows are not included in the calculation» - група «аркуш · таблиця — кількість»,
  //    без переліку «1 (S01), 2 (S02)…». ✎ fg6 F13: ЗГОРНУТИЙ за замовчуванням (пам'ять згортання чиста) - розгортаємо.
  await page.evaluate(() => {
    try {
      // fg6: ключ - для кожного користувача (`ecr.unmatchedRows.collapsed:<userId>`).
      for (const key of Object.keys(window.localStorage)) {
        if (key.startsWith('ecr.unmatchedRows.collapsed')) window.localStorage.removeItem(key);
      }
    } catch {
      // Сховище недоступне — банер однаково згорнутий за замовчуванням.
    }
  });
  await page.reload();
  const banner = page.getByTestId('document-unmatched-rows');
  await expect(banner).toBeVisible({ timeout: 60_000 });
  await expect(banner).toContainText('Some rows are not included in the calculation');
  await expect(page.getByTestId('document-unmatched-rows-summary')).toContainText('Not calculated: 1 row in 1 table');
  const toggle = page.getByTestId('document-unmatched-rows-toggle');
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await expect(page.getByTestId('document-unmatched-rows-hint')).toBeHidden();
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-expanded', 'true');
  const group = banner.getByTestId('document-unmatched-rows-group');
  await expect(group).toHaveCount(1);
  await expect(group).toContainText('Sheet 1 · Calculated table — 1 row');
  // Назва методики з підписом (fresh-eyes P3 08.11), а не голий код.
  await expect(group).toContainText('Methodology: ');
  // b3 (рішення координатора 08.11): допустимі значення правил таблиці й поточне значення рядка.
  await expect(group.getByTestId('document-unmatched-rows-allowed')).toContainText('Calculated only for «KIND»: Gas');

  // b3: одне речення «чому і як» - рівно один раз, без суперечливого «nothing to fix» (fresh-eyes P2 08.11).
  const bannerText = (await banner.textContent()) ?? '';
  expect(bannerText.split('not calculated because').length - 1, '«чому» - один раз').toBe(1);
  expect(bannerText, 'без «nothing to fix» поруч із дією').not.toContain('nothing to fix');
  await expect(page.getByTestId('document-unmatched-rows-hint')).toContainText('press F9');

  // b2: клік по рядку - сітка таблиці TCALC, фокус і підсвітка саме рядка Coal (№ 2).
  const link = group.getByTestId('document-unmatched-row-link');
  await expect(link).toHaveText('Row 2: «Coal»');
  await expect(link).toHaveAttribute('data-row-key', arranged.coalRowKey);
  await link.click();
  await expect(calcItem).toHaveAttribute('aria-selected', 'true', { timeout: 30_000 });
  await expect(page.locator('.ecr-cell-nav-target'), 'підсвічена комірка рядка').toHaveCount(1, { timeout: 30_000 });

  // b4: згорнути, перезавантажити - лишається згорнутим, кількість видно.
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await page.reload();
  await expect(page.getByTestId('document-unmatched-rows-toggle')).toHaveAttribute('aria-expanded', 'false', { timeout: 60_000 });
  await expect(page.getByTestId('document-unmatched-rows-summary')).toContainText('Not calculated: 1 row');
  await expect(page.getByTestId('document-unmatched-rows-hint')).toBeHidden();

  // Стара жовта панель AN-69 з тими самими рядками більше не дублює банер.
  await expect(page.getByTestId('calculation-issues-panel')).toHaveCount(0);
}
