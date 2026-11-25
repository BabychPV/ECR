import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { expect, test, type APIResponse, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { signIn } from './multiSheet';

/**
 * Методологія як скрипт (MS, T4) на живому стенді (`tools/e2e-stand.ps1`, `-Grep "methodology script"`):
 * редактор скрипта від першого символа до числа в колонці документа.
 *
 *   1. фікстура через публічний API: шаблон (таблиця `TSC…`: A Decimal в `t`, KIND String, OUT Calculated — код збігається
 *      з виходом скрипта) → проєкт → документ з одним рядком (A = 4) → методолог з грантом на проєкт → методологія
 *      і версія з рушієм `Script`;
 *   2. сторінка `/admin/methodologies/:id/versions/:vid/script`: набрати скрипт із помилкою (`row.Missing`) → у «Problems»
 *      діагностика `unknownColumn` → виправити → «Save» (текст на сервері) → «Preview» на документі: рядок «Selected» і
 *      число 8 → «Create test from this row» → «Save» тесту → «Publish» (причина й дата) → версія опублікована;
 *   3. перерахунок документа (API) → значення 8 у колонці `OUT` сітки документа.
 *
 * ⚠ Підписи кнопок і вкладок — англомовні рядки каталогу: `methodologies.save` («Save»), `methodologies.publish`
 * («Publish»), `methodologyScript.problems.title` («Problems»), `methodologyScript.preview.run` («Calculate»),
 * `methodologyScript.preview.createTest` («Create test from this row»). Без ключів `methodologyScript.*` у сіді підписи
 * з'являються як `⟦ключ⟧` — і спец падає на першому ж кроці з названим підписом.
 * ⚠ Дані одноразові (як `calculationIssues.spec.ts`): методологію й опубліковану версію API не видаляє.
 * ⛔ Без стенда файл ПАДАЄ (вхід/запити до API), а не пропускається (honesty-guard).
 */
test.describe.configure({ timeout: 420_000 });

const Stamp = Date.now().toString(36).toUpperCase().slice(-6);
const TableCode = `TSC${Stamp}`;
const OutputCode = 'OUT';
const EditorRole = `E2EMS${Stamp}`;
const Editor = {
  userName: `e2e-ms-${Stamp.toLowerCase()}`,
  issued: 'E2E-Meth0d-2026!',
  password: 'E2E-Meth0d-Work-2026!',
};
const EditorPermissions = [
  'Calculation.View',
  'Calculation.EditFormula',
  'Calculation.EditRule',
  // ✎ P-1124-1 UI-прохід: вкладка «Required columns» (додати вимогу) — окреме право.
  'Calculation.ManageRequiredInputs',
  'Calculation.Publish',
  'Calculation.Recalculate',
  'Template.View',
  'Document.View',
];

const BadScript = `rows from ${TableCode}; output ${OutputCode} : t = row.Missing * 2;`;
const GoodScript = `rows from ${TableCode}; output ${OutputCode} : t = row.A * 2;`;

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

function column(ordinal: number, header: string, dataType: string, unitId: number | null = null): Record<string, unknown> {
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
    unitId,
  };
}

async function editorContext(browser: Browser, password: string): Promise<BrowserContext> {
  const context = await browser.newContext();
  const login = await context.request.post('/api/v1/login/local', { data: { userName: Editor.userName, password } });
  expect(login.ok(), `вхід методолога: ${String(login.status())} ${await login.text()}`).toBe(true);

  return context;
}

/** Роль і користувач-методолог; пароль одразу змінюється з разового на робочий. */
async function createEditor(page: Page, browser: Browser): Promise<{ roleId: number; userId: number }> {
  const role = await ok<{ roleId: number }>(
    await page.request.post('/api/v1/roles', {
      data: { code: EditorRole, nameL10n: { en: 'E2E methodology script editor' }, permissionCodes: EditorPermissions },
    }),
    'роль методолога',
  );
  const user = await ok<{ userId: number }>(
    await page.request.post('/api/v1/users', {
      data: {
        userName: Editor.userName,
        provider: 'Local',
        sid: null,
        displayName: 'E2E methodology script editor',
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

interface Arranged {
  readonly documentId: number;
  readonly businessKey: string;
  readonly periodKey: number;
  readonly methodologyId: number;
  readonly versionId: number;
  readonly context: BrowserContext;
}

/** Шаблон, документ із рядком, грант методологу, методологія з порожньою версією-скриптом. */
async function arrange(page: Page, browser: Browser, roleId: number): Promise<Arranged> {
  const stamp = String(Date.now());
  const units = await ok<{ id: number; code: string }[]>(await page.request.get('/api/v1/units'), 'одиниці');
  const unitId = (units.find((unit) => unit.code === 't') ?? units[0])?.id ?? 0;
  expect(unitId, 'у стенда немає одиниць').toBeGreaterThan(0);

  // 1. Шаблон: аркуш S1 з таблицею TSC…: A (Decimal, т), KIND, OUT (Calculated).
  const template = await ok<{ templateId: number }>(
    await page.request.post('/api/v1/templates', { data: { code: `E2EMs${stamp}`, nameL10n: { en: `E2E methodology script ${stamp}` } } }),
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
    await page.request.put(`${base}/sheets/S1/tables/${TableCode}`, {
      data: { nameL10n: { en: 'Script table' }, ordinal: 0, layoutKind: 'PerPeriodInstance', rowMode: 'Dynamic', maxDynamicRows: null },
    }),
    'таблиця',
  );
  for (const [index, [code, dataType, unit]] of (
    [
      ['A', 'Decimal', unitId],
      ['KIND', 'String', null],
      // ⛔ Приймач виходу методології — тип Calculated; код збігається з виходом скрипта (прив'язка при публікації).
      [OutputCode, 'Calculated', null],
    ] as const
  ).entries()) {
    await ok(
      await page.request.put(`${base}/tables/${String(table.id)}/columns/${code}`, { data: column(index, code, dataType, unit) }),
      `колонка ${code}`,
    );
  }
  await ok(await page.request.post(`${base}/publish`, { data: { reason: 'e2e: methodology script' } }), 'публікація шаблону');

  // 2. Проєкт і документ.
  const policies = await ok<{ id: number; code: string }[]>(await page.request.get('/api/v1/projects/period-policies'), 'політики періодів');
  const policy = policies.find((item) => item.code === 'ECR-Standard') ?? policies[0];
  expect(policy, 'у стенда немає жодної політики періодів').toBeDefined();
  const project = await ok<{ projectId: number }>(
    await page.request.post('/api/v1/projects', {
      data: {
        code: `E2EMsPrj${stamp}`,
        nameL10n: { en: `E2E methodology script ${stamp}` },
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
  const summary = await ok<{ businessKey: string }>(await page.request.get(`${documentUrl}?periodKey=${String(periodKey)}`), 'документ (підсумок)');
  const tables = await ok<{ tableInstanceId: number; tableCode: string }[]>(
    await page.request.get(`${documentUrl}/tables?periodKey=${String(periodKey)}`),
    'таблиці документа',
  );
  const tableInstanceId = tables.find((item) => item.tableCode === TableCode)?.tableInstanceId ?? 0;
  expect(tableInstanceId, `екземпляр таблиці ${TableCode}`).toBeGreaterThan(0);

  // 3. Один рядок: A = 4 → вихід OUT = A * 2 = 8.
  await ok(await page.request.post(`${documentUrl}/rows`, { data: { tableInstanceId, rowKey: null } }), 'рядок');
  const slice = await ok<{ rows: { rowKey: string; rowVersion: string }[] }>(
    await page.request.get(`${documentUrl}/tables/${String(tableInstanceId)}`),
    'зріз таблиці',
  );
  const row = slice.rows[0];
  expect(row, 'у таблиці має бути рядок').toBeDefined();
  await ok(
    await page.request.patch(`${documentUrl}/cells`, {
      data: {
        tableInstanceId,
        periodKey,
        origin: 'UserEdit',
        rows: [{ rowKey: row?.rowKey, baseVersion: row?.rowVersion, cells: [{ columnCode: 'A', value: '4', isEmpty: false }] }],
      },
    }),
    'значення рядка',
  );

  // 4. Методолог бачить документ проєкту (грант Read) — інакше перегляд скрипта на документі недоступний.
  const grants = await page.request.get(`/api/v1/roles/${String(roleId)}/grants`);
  expect(grants.ok(), `GET grants: ${String(grants.status())}`).toBe(true);
  const etag = grants.headers()['etag'];
  if (etag === undefined) throw new Error('GET /roles/{id}/grants не віддав ETag');
  await ok(
    await page.request.put(`/api/v1/roles/${String(roleId)}/grants`, {
      headers: { 'If-Match': etag },
      data: { grants: [{ resourceKind: 'Project', resourceId: project.projectId, level: 'Read', isDeny: false }] },
    }),
    'грант методолога на проєкт',
  );

  // ⚠ Зміна грантів ролі робить наявний сеанс методолога недійсним (401 securityStampStale) — методолог входить ПІСЛЯ грантів, новим контекстом (вхід зі старою cookie теж дає 401).
  const context = await editorContext(browser, Editor.password);
  const editor = context.request;

  // 5. Методологія й порожня версія-скрипт (рушій — явно).
  const methodology = await ok<{ id: number }>(
    await editor.post('/api/v1/methodologies', {
      data: { code: `E2EMS_${stamp}`, group: null, kind: 'DataDriven', nameL10n: { en: `E2E methodology script ${stamp}` } },
    }),
    'методологія',
  );
  const draft = await ok<{ id: number }>(
    await editor.post(`/api/v1/methodologies/${String(methodology.id)}/versions`, {
      data: { versionNumber: '1.0', copyFromVersionId: null, level: 'Configuration', engine: 'Script' },
    }),
    'чернетка-скрипт',
  );

  return { documentId: document.documentId, businessKey: summary.businessKey, periodKey, methodologyId: methodology.id, versionId: draft.id, context };
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

/** Знімок для UI-проходу: вкладення тесту, а за `ECR_E2E_SHOTS_DIR` — ще й файл. */
async function shot(page: Page, name: string): Promise<void> {
  const body = await page.screenshot({ fullPage: false });
  await test.info().attach(name, { body, contentType: 'image/png' });
  const dir = process.env['ECR_E2E_SHOTS_DIR'] ?? '';
  if (dir !== '') {
    mkdirSync(dir, { recursive: true });
    await page.screenshot({ path: path.join(dir, `${name}.png`) });
  }
}

/** Замінює весь текст редактора скрипта (Monaco): вставка без клавіш — без спливаючих підказок. */
async function typeScript(page: Page, text: string): Promise<void> {
  const editor = page.getByTestId('script-editor');
  await expect(editor.locator('.monaco-editor').first(), 'редактор скрипта не завантажився').toBeVisible({ timeout: 60_000 });
  await editor.locator('.view-lines').first().click();
  await page.keyboard.press('ControlOrMeta+A');
  await page.keyboard.insertText(text);
}

/** Індекс колонки в сітці за текстом її заголовка (RevoGrid). */
async function gridColumn(page: Page, header: string): Promise<string> {
  const cell = page.locator('revo-grid revogr-header .rgHeaderCell', { hasText: header }).first();
  await expect(cell, `заголовка «${header}» немає в сітці`).toBeVisible({ timeout: 60_000 });
  const index = await cell.getAttribute('aria-colindex');
  if (index === null) throw new Error(`заголовок «${header}» без aria-colindex`);

  return index;
}

test('methodology script: type with an error → fix → save → preview shows the number → test from row → publish → recalculation fills the column', async ({ page, browser }) => {
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

  const arranged = await arrange(page, browser, ids.roleId);
  const context = arranged.context;
  try {
    const scriptUrl = `/api/v1/methodologies/${String(arranged.methodologyId)}/versions/${String(arranged.versionId)}/script`;

    const ui = await context.newPage();
    await ui.setViewportSize({ width: 1440, height: 900 });
    // ✎ P-1124-1 UI-прохід: час відкриття / збереження / перевірки — в анотацію тесту й журнал (>1 с — розбирати).
    const timings: string[] = [];
    const opened = Date.now();
    await ui.goto(`/admin/methodologies/${String(arranged.methodologyId)}/versions/${String(arranged.versionId)}/script`);

    // 1. Нова версія-скрипт відкрилась у редакторі зі стартовою заготовкою; кнопка «Save» ще вимкнена (нічого не змінено).
    const save = ui.getByRole('button', { name: 'Save', exact: true });
    await expect(ui.getByTestId('script-editor').locator('.monaco-editor').first()).toBeVisible({ timeout: 60_000 });
    timings.push(`editor open ${String(Date.now() - opened)} ms`);
    await expect(save).toBeDisabled();

    // 2. Скрипт із помилкою: невідома колонка → діагностика в «Problems» за ~0,5 с, текст ще не збережено.
    await typeScript(ui, BadScript);
    await expect(ui.getByTestId('script-unsaved')).toBeVisible();
    await ui.getByRole('tab', { name: /Problems/ }).click();
    const problems = ui.getByTestId('script-problems');
    await expect(problems).toContainText(/Column Missing was not found/, { timeout: 30_000 });

    // 3. Виправлення: перелік порожній, «Save» записує текст на сервері.
    await typeScript(ui, GoodScript);
    await expect(ui.getByRole('tabpanel', { name: /Problems/ }).getByText('No problems found.')).toBeVisible({ timeout: 30_000 });
    await expect(save).toBeEnabled();
    await save.click();
    await expect(ui.getByTestId('script-unsaved')).toBeHidden({ timeout: 30_000 });
    await expect(save).toBeDisabled();
    const stored = await ok<{ text: string; engine: string }>(await ui.request.get(scriptUrl), 'збережений скрипт');
    expect(stored.engine).toBe('Script');
    expect(stored.text).toBe(GoodScript);

    // 4. Перегляд на документі: документ і період → «Calculate» → рядок «Selected» і число 8.
    await ui.getByRole('tab', { name: 'Preview' }).click();
    const documentField = ui.getByRole('textbox', { name: 'Document', exact: true });
    await documentField.click();
    await documentField.fill(arranged.businessKey);
    await ui.getByRole('option', { name: new RegExp(arranged.businessKey) }).first().click();
    const period = ui.getByRole('textbox', { name: 'Period', exact: true });
    await period.fill(String(arranged.periodKey));
    await period.press('Tab');
    await ui.getByRole('button', { name: 'Calculate' }).click();
    const preview = ui.getByTestId('script-preview');
    await expect(preview, 'перегляд не показав таблицю').toBeVisible({ timeout: 60_000 });
    await expect(preview).toContainText(TableCode);
    await expect(preview.getByText('Selected', { exact: true })).toBeVisible();
    await expect(preview.getByRole('cell', { name: /^8(\.0+)?$/ }), 'у перегляді має бути OUT = 8').toBeVisible();

    // 5. Тест із рядка: розгорнути рядок → «Create test from this row» → форма з очікуванням OUT = 8 → «Save».
    await preview.getByRole('button', { name: /Show all values of row 1/ }).click();
    await preview.getByRole('button', { name: 'Create test from this row' }).click();
    const dialog = ui.getByRole('dialog');
    await expect(dialog.getByLabel('Code', { exact: true })).toHaveValue('TEST_1');
    await expect(dialog.getByTestId('script-test-issues')).toHaveCount(0);
    await dialog.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(ui.getByTestId('script-tests')).toContainText('TEST_1', { timeout: 30_000 });
    await expect(ui.getByTestId('script-tests')).toContainText(`${OutputCode} = 8`);

    // 5a. ✎ P-1124-1 UI-прохід (24.11): «Inputs & outputs» показує вхід A і вихід OUT.
    await ui.getByRole('tab', { name: 'Inputs & outputs' }).click();
    const io = ui.getByRole('tabpanel', { name: 'Inputs & outputs' });
    await expect(io).toContainText(OutputCode, { timeout: 30_000 });
    await shot(ui, 'ms-ui-io-light');

    // 5b. «Required columns»: додати вимогу A (Block) — підпис колонки без повтору коду («A (A) · …» — дефект).
    await ui.getByRole('tab', { name: 'Required columns' }).click();
    const required = ui.getByRole('tabpanel', { name: 'Required columns' });
    await required.getByRole('button', { name: 'Add required input' }).click();
    const requiredDialog = ui.getByRole('dialog');
    const columnField = requiredDialog.getByRole('textbox', { name: 'Column' });
    await columnField.click();
    await columnField.fill('A');
    const option = ui.getByRole('option', { name: new RegExp(`· S1/${TableCode}$`) }).first();
    await expect(option).toBeVisible({ timeout: 30_000 });
    await expect(ui.getByRole('option', { name: /\(A\)/ })).toHaveCount(0);
    await option.click();
    await requiredDialog.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(requiredDialog).toBeHidden({ timeout: 30_000 });
    await expect(required.getByRole('table')).toContainText('A', { timeout: 30_000 });

    // Змінити (підказка) → у переліку; завести KIND → прибрати з підтвердженням → KIND зник, A лишилась.
    await required.getByRole('button', { name: 'Edit', exact: true }).first().click();
    const editDialog = ui.getByRole('dialog');
    await editDialog.getByRole('textbox', { name: 'Hint' }).fill('e2e: A is required');
    await editDialog.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(editDialog).toBeHidden({ timeout: 30_000 });
    await expect(required.getByRole('table')).toContainText('e2e: A is required', { timeout: 30_000 });

    await required.getByRole('button', { name: 'Add required input' }).click();
    const kindDialog = ui.getByRole('dialog');
    const kindField = kindDialog.getByRole('textbox', { name: 'Column' });
    await kindField.click();
    await kindField.fill('KIND');
    await ui.getByRole('option', { name: new RegExp(`^KIND · S1/${TableCode}$`) }).first().click();
    await kindDialog.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(kindDialog).toBeHidden({ timeout: 30_000 });
    const kindRow = required.getByRole('row', { name: /KIND/ });
    await expect(kindRow).toBeVisible({ timeout: 30_000 });
    await kindRow.getByRole('button', { name: 'Remove' }).click();
    const confirmRemove = ui.getByRole('dialog', { name: 'Remove the required input column?' });
    await confirmRemove.getByRole('button', { name: 'Remove' }).click();
    await expect(confirmRemove).toBeHidden({ timeout: 30_000 });
    await expect(required.getByRole('row', { name: /KIND/ })).toHaveCount(0, { timeout: 30_000 });
    await expect(required.getByRole('table')).toContainText('A');
    await shot(ui, 'ms-ui-required-light');

    // 5c. Код на весь екран: незбережена правка, курсор і «Problems» переживають вхід/вихід; Ctrl+S працює всередині.
    await ui.getByRole('tab', { name: /Problems/ }).click();
    const pane = ui.getByTestId('script-editor-pane');
    await ui.getByTestId('script-editor').locator('.view-lines').first().click();
    await ui.keyboard.press('ControlOrMeta+End');
    await ui.keyboard.insertText(' ');
    await expect(ui.getByTestId('script-unsaved')).toBeVisible();
    const toggle = ui.getByTestId('script-fullscreen-toggle');
    let started = Date.now();
    await toggle.click();
    await expect(pane).toHaveAttribute('data-fullscreen', 'true');
    timings.push(`fullscreen enter ${String(Date.now() - started)} ms`);
    await expect(ui.getByTestId('script-unsaved')).toBeVisible();
    await expect(ui.getByTestId('script-status')).toBeVisible();
    const box = await ui.getByTestId('script-editor').boundingBox();
    expect(box?.height ?? 0, 'Monaco перерозкладено на висоту вікна').toBeGreaterThan(600);
    await shot(ui, 'ms-ui-fullscreen-light');
    await ui.keyboard.press('Escape');
    await expect(pane).toHaveAttribute('data-fullscreen', 'false');
    await expect(toggle, 'фокус повертається на кнопку').toBeFocused();
    await expect(ui.getByTestId('script-unsaved')).toBeVisible();
    await ui.keyboard.press('ControlOrMeta+Shift+F');
    await expect(pane).toHaveAttribute('data-fullscreen', 'true');
    await ui.getByTestId('script-editor').locator('.view-lines').first().click();
    started = Date.now();
    await ui.keyboard.press('ControlOrMeta+S');
    await expect(ui.getByTestId('script-unsaved')).toBeHidden({ timeout: 30_000 });
    timings.push(`save ${String(Date.now() - started)} ms`);
    await ui.keyboard.press('Escape');
    await expect(pane).toHaveAttribute('data-fullscreen', 'false');

    // 6. Публікація: причина й дата чинності раніше за період (версію добирають за датою періоду).
    await ui.getByRole('button', { name: 'Publish', exact: true }).click();
    const publish = ui.getByRole('dialog', { name: /Publish/ });
    await publish.getByLabel('Reason for the change').fill('e2e: methodology script');
    const date = publish.getByRole('textbox', { name: 'Effective from' });
    await date.click();
    await date.press('ControlOrMeta+A');
    await date.pressSequentially(`${String(Math.floor(arranged.periodKey / 100) - 1)}-01-01`);
    await date.press('Tab');
    await publish.getByRole('button', { name: 'Publish', exact: true }).click();
    await expect(publish).toBeHidden({ timeout: 120_000 });

    const versions = await ok<{ id: number; status: string; engine?: string }[]>(
      await ui.request.get(`/api/v1/methodologies/${String(arranged.methodologyId)}/versions`),
      'версії методології',
    );
    expect(versions.find((item) => item.id === arranged.versionId)?.status).toBe('Published');

    // Опублікована версія — лише читання: «Save» зникла, редактор на місці.
    await ui.goto(`/admin/methodologies/${String(arranged.methodologyId)}/versions/${String(arranged.versionId)}/script`);
    await expect(ui.getByTestId('script-editor')).toBeVisible({ timeout: 60_000 });
    await expect(ui.getByRole('button', { name: 'Save', exact: true })).toHaveCount(0);

    // 7. Перерахунок документа → значення 8 у колонці OUT сітки.
    await recalculate(page, arranged);
    await ui.goto(`/documents/${String(arranged.documentId)}?periodKey=${String(arranged.periodKey)}`);
    const index = await gridColumn(ui, OutputCode);
    const cell = ui.locator(`revo-grid revogr-data[type="rgRow"] .rgCell[data-rgrow="0"][aria-colindex="${index}"]`);
    await expect(cell, 'у колонці OUT має бути 8 після перерахунку').toHaveText(/^\s*8([.,]0+)?\s*$/, { timeout: 60_000 });

    // 8. ✎ P-1124-1: вимога A (Block) опублікованої версії-скрипта (без правил відбору) справді блокує запис рядка
    //    без A у документі — ECR-CALC-0437 (до фіксу gate мовчки пропускав версію без правил).
    const documentUrl = `/api/v1/documents/${String(arranged.documentId)}`;
    const tables = await ok<{ tableInstanceId: number; tableCode: string }[]>(
      await page.request.get(`${documentUrl}/tables?periodKey=${String(arranged.periodKey)}`),
      'таблиці документа',
    );
    const tableInstanceId = tables.find((item) => item.tableCode === TableCode)?.tableInstanceId ?? 0;
    const before = await ok<{ rows: { rowKey: string }[] }>(
      await page.request.get(`${documentUrl}/tables/${String(tableInstanceId)}`),
      'зріз таблиці до рядка',
    );
    await ok(await page.request.post(`${documentUrl}/rows`, { data: { tableInstanceId, rowKey: null } }), 'другий рядок');
    const slice = await ok<{ rows: { rowKey: string; rowVersion: string }[] }>(
      await page.request.get(`${documentUrl}/tables/${String(tableInstanceId)}`),
      'зріз таблиці',
    );
    const second = slice.rows.find((row) => !before.rows.some((old) => old.rowKey === row.rowKey));
    expect(second, 'має бути другий рядок').toBeDefined();
    const refused = await page.request.patch(`${documentUrl}/cells`, {
      data: {
        tableInstanceId,
        periodKey: arranged.periodKey,
        origin: 'UserEdit',
        rows: [{ rowKey: second?.rowKey, baseVersion: second?.rowVersion, cells: [{ columnCode: 'KIND', value: 'Gas', isEmpty: false }] }],
      },
    });
    const refusedBody = await refused.text();
    expect(refused.status(), `рядок без обов'язкового A: ${refusedBody}`).toBeGreaterThanOrEqual(400);
    expect(refusedBody).toContain('ECR-CALC-0437');

    // 9. Нова версія з переліку версій: свіжа чернетка зі стартової заготовки — 0 проблем; «Compare» відкривається.
    started = Date.now();
    await ui.goto(`/admin/methodologies/${String(arranged.methodologyId)}/versions`);
    await expect(ui.getByTestId('current-script-version')).toBeVisible({ timeout: 60_000 });
    timings.push(`versions page ${String(Date.now() - started)} ms`);
    await ui.getByRole('button', { name: 'New version', exact: true }).click();
    const newVersion = ui.getByRole('dialog', { name: 'New draft version' });
    await newVersion.getByLabel('Version number').fill('2.0');
    await newVersion.getByRole('button', { name: 'Create draft' }).click();
    started = Date.now();
    await ui.waitForURL(/\/versions\/\d+\/script$/, { timeout: 60_000 });
    await expect(ui.getByTestId('script-editor').locator('.monaco-editor').first()).toBeVisible({ timeout: 60_000 });
    await expect(ui.getByTestId('script-status')).toHaveText('No problems found.', { timeout: 30_000 });
    timings.push(`new draft open+validate ${String(Date.now() - started)} ms`);
    await ui.getByRole('tab', { name: /Problems/ }).click();
    await expect(ui.getByRole('tabpanel', { name: /Problems/ }).getByText('No problems found.')).toBeVisible();
    await shot(ui, 'ms-ui-new-draft-light');

    await ui.getByRole('button', { name: 'Compare with version…' }).click();
    const compare = ui.getByRole('dialog');
    await expect(compare).toBeVisible({ timeout: 30_000 });
    await shot(ui, 'ms-ui-compare-light');
    await ui.keyboard.press('Escape');
    await expect(compare).toBeHidden({ timeout: 10_000 });

    // «Edit constant as table» без константи під курсором — пояснення, а не тиша.
    // ⚠ Кнопка навмисно `aria-disabled` (а не `disabled`): лишається клікабельною й пояснює — Playwright вважає
    // її вимкненою, тож клік примусовий.
    const constButton = ui.getByRole('button', { name: 'Edit constant as table' });
    await expect(constButton).toHaveAttribute('aria-disabled', 'true');
    await constButton.click({ force: true });
    await expect(ui.getByText('Place the cursor inside a constant map { … } to edit it as a table.').first()).toBeVisible({ timeout: 10_000 });

    // 10. Темна тема: той самий редактор і код на весь екран.
    await ok(
      await ui.request.put('/api/v1/me/preferences/theme', { headers: { 'Content-Type': 'application/json' }, data: JSON.stringify('dark') }),
      'темна тема',
    );
    await ui.reload();
    await expect(ui.getByTestId('script-editor').locator('.monaco-editor').first()).toBeVisible({ timeout: 60_000 });
    await shot(ui, 'ms-ui-editor-dark');
    await ui.getByTestId('script-fullscreen-toggle').click();
    await expect(ui.getByTestId('script-editor-pane')).toHaveAttribute('data-fullscreen', 'true');
    await shot(ui, 'ms-ui-fullscreen-dark');
    await ui.keyboard.press('Escape');
    await ui.request.put('/api/v1/me/preferences/theme', { headers: { 'Content-Type': 'application/json' }, data: JSON.stringify('light') });

    test.info().annotations.push({ type: 'timings', description: timings.join('; ') });
    console.log(`[ms-ui-pass] timings: ${timings.join('; ')}`);
  } finally {
    await context.close();
  }
}
