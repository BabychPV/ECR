import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { expect, test, type APIRequestContext, type APIResponse, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { signIn } from './multiSheet';

/**
 * ✎ 25.11 (живі проходи: таблиця 3.00 щойно створеного документа і документ 100 LAND_UI) — попередження «обов'язкові
 * колонки методології» (`ECR-CALC-0437`, Warn) у сітці документа «рік» (`YearWithMonthColumns`) на РЕАЛЬНОМУ стенді
 * (`tools/e2e-stand.ps1 -Grep requiredInputsBanner`).
 *
 * До фіксу над сіткою ріс розгорнутий банер: заголовок «4 required column(s) missing», під ним 131–137 рядків (той
 * самий рядок 50 разів, уже заповнені комірки не зникали), коди колонок, українською в англійському інтерфейсі, без
 * номерів рядків, і сітка з'їжджала вниз. Тут перевіряється кожен пункт:
 *  1. згорнуто до однієї кнопки в рядку інструментів (`aria-expanded=false`), сітка НЕ рухається ні від появи, ні від
 *     розкриття переліку;
 *  2. без повторів: дві методології з тією самою вимогою й повторні збереження — рядок на колонку;
 *  3. з поточних даних: заповнена в сітці колонка зникає з переліку;
 *  4. лічильник на кнопці = кількість рядків переліку;
 *  5. назви колонок (заголовки), номери рядків, коди методологій — без кодів колонок і без кирилиці в англійському UI;
 *  6. мова інтерфейсу (ru) — російський текст; 7. позначка на самих порожніх комірках; 8. axe light/dark.
 *
 * ⚠ Фікстура через API (як `yearLayout.spec.ts` + `methodologyScript.spec.ts`): шаблон «рік», дві опубліковані
 * методології-скрипти з вимогами Warn до «Type of fuel» і «Power», документ із двома рядками. Дані одноразові.
 * ⛔ Без стенда файл ПАДАЄ (вхід/API), а не пропускається (honesty-guard).
 */
test.describe.configure({ mode: 'serial', timeout: 600_000 });

const Stamp = Date.now().toString(36).toUpperCase().slice(-6);
const TableCode = `TRQ${Stamp}`;
const Headers = { NAME: 'Equipment name', KIND: 'Type of fuel', A: 'Power', OUT: 'Result' } as const;

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

function column(ordinal: number, header: string, dataType: string, month: number | null = null, unitId: number | null = null): Record<string, unknown> {
  return {
    headerL10n: { en: header, ru: `${header} (ru)` },
    ordinal,
    dataType,
    isRequired: false,
    isReadOnly: false,
    isHidden: false,
    precision: dataType === 'Decimal' ? 18 : null,
    scale: dataType === 'Decimal' ? 2 : null,
    defaultValue: null,
    displayFormat: null,
    lookupFilter: null,
    lookupRegistryDefId: null,
    styleId: null,
    unitId,
    ...(month === null ? {} : { isMonthColumn: true, monthNumber: month }),
  };
}

interface Arranged {
  readonly documentId: number;
  readonly anchor: number;
  readonly methodologies: readonly string[];
}

/** Методолог (як у `methodologyScript.spec.ts`): у e2e-admin немає `Calculation.ManageRequiredInputs`. */
const EditorRole = `E2ERQ${Stamp}`;
const Editor = { userName: `e2e-rq-${Stamp.toLowerCase()}`, issued: 'E2E-Req1n-2026!', password: 'E2E-Req1n-Work-2026!' };
const EditorPermissions = [
  'Calculation.View',
  'Calculation.EditFormula',
  'Calculation.EditRule',
  'Calculation.ManageRequiredInputs',
  'Calculation.Publish',
  'Template.View',
  'Document.View',
];

async function editorRequest(browser: Browser, password: string): Promise<BrowserContext> {
  const context = await browser.newContext();
  const login = await context.request.post('/api/v1/login/local', { data: { userName: Editor.userName, password } });
  expect(login.ok(), `вхід методолога: ${String(login.status())} ${await login.text()}`).toBe(true);

  return context;
}

/** Роль і користувач-методолог; разовий пароль одразу змінюється на робочий. */
async function createEditor(request: APIRequestContext, browser: Browser): Promise<{ roleId: number; userId: number }> {
  const role = await ok<{ roleId: number }>(
    await request.post('/api/v1/roles', {
      data: { code: EditorRole, nameL10n: { en: 'E2E required inputs methodologist' }, permissionCodes: EditorPermissions },
    }),
    'роль методолога',
  );
  const user = await ok<{ userId: number }>(
    await request.post('/api/v1/users', {
      data: {
        userName: Editor.userName,
        provider: 'Local',
        sid: null,
        displayName: 'E2E required inputs methodologist',
        initialPassword: Editor.issued,
        roleCodes: [EditorRole],
      },
    }),
    'користувач-методолог',
  );
  const first = await editorRequest(browser, Editor.issued);
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

async function arrange(request: APIRequestContext, editor: APIRequestContext): Promise<Arranged> {
  const stamp = String(Date.now());
  const year = new Date().getUTCFullYear() - 1;
  const units = await ok<{ id: number; code: string }[]>(await request.get('/api/v1/units'), 'одиниці');
  const unitId = (units.find((unit) => unit.code === 't') ?? units[0])?.id ?? null;

  const template = await ok<{ templateId: number }>(
    await request.post('/api/v1/templates', { data: { code: `E2EReq${stamp}`, nameL10n: { en: `E2E required ${stamp}` } } }),
    'шаблон',
  );
  const version = await ok<{ versionId: number }>(
    await request.post(`/api/v1/templates/${String(template.templateId)}/versions`, { data: { versionNumber: '1.0.0.0', cloneFromVersionId: null } }),
    'версія',
  );
  const base = `/api/v1/template-versions/${String(version.versionId)}`;
  await ok(await request.put(`${base}/layout`, { data: { layout: 'YearWithMonthColumns' } }), 'розкладка «рік»');
  await ok(
    await request.put(`${base}/sheets/S1`, { data: { nameL10n: { en: 'Equipment' }, ordinal: 1, isMandatory: true, isVisible: true, sheetGroup: null } }),
    'аркуш',
  );
  const table = await ok<{ id: number }>(
    await request.put(`${base}/sheets/S1/tables/${TableCode}`, {
      data: { nameL10n: { en: 'Equipment table' }, ordinal: 0, layoutKind: 'MonthsInColumns', rowMode: 'Dynamic', maxDynamicRows: 20 },
    }),
    'таблиця',
  );
  const columns: [string, Record<string, unknown>][] = [
    ['NAME', column(0, Headers.NAME, 'String')],
    ['KIND', column(1, Headers.KIND, 'String')],
    ['A', column(2, Headers.A, 'Decimal', null, unitId)],
    ['OUT', column(3, Headers.OUT, 'Calculated')],
    ...Array.from({ length: 12 }, (_, i): [string, Record<string, unknown>] => [
      `M${String(i + 1).padStart(2, '0')}`,
      column(i + 4, `Month ${String(i + 1).padStart(2, '0')}`, 'Decimal', i + 1),
    ]),
  ];
  for (const [code, body] of columns) {
    await ok(await request.put(`${base}/tables/${String(table.id)}/columns/${code}`, { data: body }), `стовпець ${code}`);
  }
  await ok(await request.post(`${base}/publish`, { data: { reason: 'e2e: required inputs banner' } }), 'публікація шаблону');
  const structure = await ok<{ sheets: { id: number }[] }>(await request.get(`${base}/structure`), 'структура');

  const policy = await ok<{ id: number }>(
    await request.post('/api/v1/projects/period-policies', {
      data: { code: `E2ERq${stamp}`, openOffsetDays: 0, graceOffsetDays: 3000, hardCloseOffsetDays: 3600, yearGraceOffsetDays: 3600 },
    }),
    'політика періодів',
  );
  const project = await ok<{ projectId: number }>(
    await request.post('/api/v1/projects', {
      data: {
        code: `E2ERqPrj${stamp}`, nameL10n: { en: `E2E required ${stamp}` }, periodKind: 'Monthly', timeZoneId: 'UTC',
        templateVersionId: version.versionId, periodPolicyId: policy.id, year,
      },
    }),
    'проєкт',
  );
  await ok(await request.post(`/api/v1/projects/${String(project.projectId)}/activate`), 'активація');

  const document = await ok<{ documentId: number }>(
    await request.post('/api/v1/documents', {
      data: { projectId: project.projectId, sheetDefIds: structure.sheets.map((s) => s.id), templateVersionId: version.versionId },
    }),
    'документ',
  );
  const anchor = year * 100 + 1;
  for (let m = 1; m <= 12; m += 1) {
    await ok(await request.get(`/api/v1/documents/${String(document.documentId)}/tables?periodKey=${String(year * 100 + m)}`), `таблиці ${String(m)}`);
  }
  const tables = await ok<{ tableInstanceId: number; tableCode: string }[]>(
    await request.get(`/api/v1/documents/${String(document.documentId)}/tables?periodKey=${String(anchor)}`),
    'таблиці якоря',
  );
  const instance = tables.find((t) => t.tableCode === TableCode)?.tableInstanceId ?? 0;
  for (const rowKey of ['R1', 'R2']) {
    await ok(await request.post(`/api/v1/documents/${String(document.documentId)}/rows`, { data: { tableInstanceId: instance, rowKey } }), `рядок ${rowKey}`);
  }
  const slice = await ok<{ columns: { id: number; code: string }[] }>(
    await request.get(`/api/v1/documents/${String(document.documentId)}/tables/${String(instance)}`),
    'зріз',
  );
  const columnId = (code: string): number => slice.columns.find((c) => c.code === code)?.id ?? 0;
  expect(columnId('KIND'), 'id колонки KIND').toBeGreaterThan(0);

  // Дві методології над таблицею (вихід → OUT, правило «усі рядки», золотий тест) з однаковими вимогами Warn до KIND і
  // A: сервер мусить звести їх в один рядок на колонку. Форма — як `calculationIssues.spec.ts`.
  const outUnit = unitId ?? 0;
  const methodologies: string[] = [];
  for (const suffix of ['A', 'B']) {
    const code = `E2ERQ_${suffix}_${Stamp}`;
    const methodology = await ok<{ id: number }>(
      await editor.post('/api/v1/methodologies', { data: { code, group: null, kind: 'DataDriven', nameL10n: { en: `E2E required ${suffix}` } } }),
      `методологія ${suffix}`,
    );
    const draft = await ok<{ id: number }>(
      await editor.post(`/api/v1/methodologies/${String(methodology.id)}/versions`, {
        data: { versionNumber: '1.0', copyFromVersionId: null, level: 'Configuration' },
      }),
      `версія ${suffix}`,
    );
    const methodologyUrl = `/api/v1/methodologies/${String(methodology.id)}`;
    const versionUrl = `${methodologyUrl}/versions/${String(draft.id)}`;
    await ok(await editor.put(`${versionUrl}/outputs/EMISSION`, { data: { unitId: outUnit, ordinal: 1 } }), `вихід ${suffix}`);
    await ok(
      await editor.put(`${versionUrl}/formulas/EMISSION`, {
        data: { expression: '@A * 2', resultType: 'Number', outputUnitId: outUnit, argumentsCsv: 'A' },
      }),
      `формула ${suffix}`,
    );
    await ok(await editor.put(`${versionUrl}/rules/ALL`, { data: { matchJson: '{}', priority: 1, isActive: true } }), `правило ${suffix}`);
    await ok(
      await editor.put(`${versionUrl}/tests/GOLDEN`, {
        data: {
          inputJson: JSON.stringify({
            documentId: document.documentId,
            tableInstanceId: instance,
            periodKey: { value: anchor },
            sourceRowKey: 'R1',
            arguments: [{ argumentCode: 'A', value: 4, valueString: null, unitId: null }],
          }),
          expectedJson: '{"EMISSION":8}',
          tolerance: 0.0001,
        },
      }),
      `золотий тест ${suffix}`,
    );
    await ok(
      await editor.put(`${methodologyUrl}/bindings/${String(columnId('OUT'))}/EMISSION`, { data: { matchJson: '{}', isActive: true } }),
      `прив'язка ${suffix}`,
    );
    for (const required of ['KIND', 'A']) {
      await ok(
        await editor.put(`${versionUrl}/required-inputs/${String(columnId(required))}`, { data: { severity: 'Warn', hintL10n: null } }),
        `вимога ${suffix}.${required}`,
      );
    }
    await ok(
      await editor.post(`${versionUrl}/publish`, { data: { changeReason: 'e2e: required inputs banner', effectiveFrom: `${String(year - 1)}-01-01` } }),
      `публікація ${suffix}`,
    );
    methodologies.push(code);
  }

  return { documentId: document.documentId, anchor, methodologies };
}

async function shot(page: Page, name: string): Promise<void> {
  const body = await page.screenshot({ fullPage: false });
  await test.info().attach(name, { body, contentType: 'image/png' });
  const dir = process.env['ECR_E2E_SHOTS_DIR'] ?? '';
  if (dir !== '') {
    mkdirSync(dir, { recursive: true });
    await page.screenshot({ path: path.join(dir, `${name}.png`) });
  }
}

async function axe(page: Page): Promise<string[]> {
  await page.addScriptTag({ path: 'node_modules/axe-core/axe.min.js' });

  return page.evaluate(async () => {
    const run = (window as unknown as {
      axe: { run: (c: Element, o: object) => Promise<{ violations: { id: string; nodes: { target: unknown[] }[] }[] }> };
    }).axe;
    const scope = document.querySelector('[data-required-inputs]')?.closest('.mantine-Group-root') ?? document.body;
    const result = await run.run(scope, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa'] } });

    return result.violations.flatMap((v) => v.nodes.map((n) => `${v.id}: ${String(n.target.join(' '))}`));
  });
}

async function preference(page: Page, key: 'theme' | 'language', value: string): Promise<void> {
  await ok(
    await page.request.put(`/api/v1/me/preferences/${key}`, { headers: { 'Content-Type': 'application/json' }, data: JSON.stringify(value) }),
    `налаштування ${key}`,
  );
}

/** Клітинка сітки: рядок (0-based) × колонка за заголовком. */
async function cell(page: Page, header: string, row: number): Promise<{ x: number; y: number }> {
  const head = page.locator('revo-grid revogr-header .rgHeaderCell', { hasText: header }).first();
  await expect(head, `заголовка «${header}» немає`).toBeVisible({ timeout: 60_000 });
  const index = await head.getAttribute('aria-colindex');
  const target = page.locator(`revo-grid revogr-data[type="rgRow"] .rgCell[data-rgrow="${String(row)}"][aria-colindex="${String(index)}"]`);
  const box = await target.boundingBox();
  if (box === null) throw new Error(`немає клітинки ${header} × ${String(row)}`);

  return { x: box.x + box.width / 2, y: box.y + box.height / 2 };
}

/**
 * Ввести значення в клітинку. ⚠ Перед введенням — пауза до кінця перерахунку попередньої правки (OUT — обчислювана
 * колонка): оновлення зрізу посеред набору перезапускало редактор RevoGrid, і з «Heater» лишалось «H». Редактор
 * відкривається Enter, значення вставляється одним `insertText`.
 */
async function type(page: Page, header: string, row: number, value: string): Promise<void> {
  await expect(page.locator('[data-save-status="saving"]')).toHaveCount(0, { timeout: 30_000 });
  await page.waitForTimeout(1_500);
  const at = await cell(page, header, row);
  await page.mouse.click(at.x, at.y);
  await page.keyboard.press('Enter');
  // Той самий шлях, що `gridUnloadLeave.spec.ts`: Enter → фокус у полі редактора → набір → Enter.
  const editor = page.locator('revo-grid').first().locator('.edit-input-wrapper input');
  await expect(editor, 'редактор клітинки не відкрився').toBeFocused({ timeout: 10_000 });
  await page.keyboard.type(value);
  await page.keyboard.press('Enter');
  await expect(page.locator('.edit-input-wrapper')).toHaveCount(0);
}

async function savedPatches(page: Page, count: number, act: () => Promise<void>): Promise<void> {
  let seen = 0;
  const listener = (response: { request: () => { method: () => string }; url: () => string }): void => {
    if (response.request().method() === 'PATCH' && response.url().includes('/cells')) seen += 1;
  };
  page.on('response', listener);
  await act();
  await expect.poll(() => seen, { timeout: 30_000, message: 'збереження не дійшло' }).toBeGreaterThanOrEqual(count);
  page.off('response', listener);
}

async function openList(page: Page): Promise<string[]> {
  const toggle = page.locator('[data-required-inputs="warning"]');
  if ((await toggle.getAttribute('aria-expanded')) !== 'true') await toggle.click();
  const list = page.locator('[data-required-inputs-list]');
  await expect(list).toBeVisible({ timeout: 10_000 });

  return list.locator('li').allTextContents();
}

test('required-columns warning: collapsed, deduplicated, live, named, localized, grid does not move', async ({ page, browser }) => {
  await signIn(page);
  await page.setViewportSize({ width: 1920, height: 1000 });
  const created = await createEditor(page.request, browser);
  const editor = await editorRequest(browser, Editor.password);
  try {
    const arranged = await arrange(page.request, editor.request);
    await preference(page, 'language', 'en');
    await preference(page, 'theme', 'light');
    await page.goto(`/documents/${String(arranged.documentId)}?periodKey=${String(arranged.anchor)}`);
    const grid = page.locator('revo-grid').first();
    await expect(grid).toBeVisible({ timeout: 60_000 });
    await expect(page.locator('[data-required-inputs]')).toHaveCount(0);
    const gridTop = async (): Promise<number> => (await grid.boundingBox())?.y ?? -1;
    const top0 = await gridTop();

    // Рядок 1: назва → сервер повертає вимоги двох методологій до KIND і A.
    await savedPatches(page, 1, () => type(page, Headers.NAME, 0, 'Boiler'));
    const toggle = page.locator('[data-required-inputs="warning"]');
    await expect(toggle).toBeVisible({ timeout: 30_000 });
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await expect(page.locator('[data-required-inputs-list]')).toHaveCount(0);
    await expect(page.locator('.mantine-Alert-root', { hasText: /required/i })).toHaveCount(0);
    await expect(toggle).toHaveText(/^2 required column\(s\) not filled yet/);
    expect(await gridTop(), 'сітка зрушила від попередження').toBe(top0);
    await shot(page, 'required-collapsed-light');

    // Рядок 2 і повторні збереження рядка 1 — нічого не накопичується.
    await savedPatches(page, 1, () => type(page, Headers.NAME, 1, 'Heater'));
    await savedPatches(page, 1, () => type(page, Headers.NAME, 0, 'Boiler 2'));
    await savedPatches(page, 1, () => type(page, Headers.NAME, 1, 'Heater 2'));
    let items = await openList(page);
    expect(items).toHaveLength(2);
    await expect(toggle).toHaveText(/^2 required column\(s\)/);
    const codes = [...arranged.methodologies].sort().join(', ');
    expect(items).toEqual([
      `${Headers.KIND} · rows: 1, 2 · needed by ${codes}`,
      `${Headers.A} · rows: 1, 2 · needed by ${codes}`,
    ]);
    expect(items.join(' ')).not.toMatch(/[Ѐ-ӿ]|\bKIND\b|\bA ·/);
    expect(await gridTop(), 'сітка зрушила від розкриття переліку').toBe(top0);
    expect(await page.locator('revo-grid .rgCell.ecr-cell-required-input-warning').count(), 'позначки на порожніх комірках').toBeGreaterThanOrEqual(4);
    await shot(page, 'required-expanded-light');
    expect(await axe(page), 'axe light').toEqual([]);
    await page.keyboard.press('Escape');

    // Заповнити KIND у рядку 1 — рядок 1 зникає з «Type of fuel»; у обох — колонка зникає зовсім.
    await savedPatches(page, 1, () => type(page, Headers.KIND, 0, 'Diesel'));
    items = await openList(page);
    expect(items[0]).toBe(`${Headers.KIND} · rows: 2 · needed by ${codes}`);
    await page.keyboard.press('Escape');
    await savedPatches(page, 1, () => type(page, Headers.KIND, 1, 'Gas'));
    items = await openList(page);
    expect(items).toEqual([`${Headers.A} · rows: 1, 2 · needed by ${codes}`]);
    await expect(toggle).toHaveText(/^1 required column\(s\)/);
    await page.keyboard.press('Escape');

    // Темна тема: той самий стан після перезавантаження (вимоги — з наступного збереження).
    await preference(page, 'theme', 'dark');
    await page.reload();
    await expect(grid).toBeVisible({ timeout: 60_000 });
    await savedPatches(page, 1, () => type(page, Headers.NAME, 0, 'Boiler 3'));
    await expect(toggle).toBeVisible({ timeout: 30_000 });
    await openList(page);
    await shot(page, 'required-expanded-dark');
    expect(await axe(page), 'axe dark').toEqual([]);
    await page.keyboard.press('Escape');

    // Мова інтерфейсу ru: кнопка й перелік російською, назва колонки — російський заголовок.
    await preference(page, 'theme', 'light');
    await preference(page, 'language', 'ru');
    await page.reload();
    await expect(grid).toBeVisible({ timeout: 60_000 });
    await savedPatches(page, 1, () => type(page, `${Headers.NAME} (ru)`, 0, 'Котёл'));
    await expect(toggle).toHaveText(/^Не заполнено обязательных столбцов: 1/, { timeout: 30_000 });
    items = await openList(page);
    expect(items).toEqual([`${Headers.A} (ru) · строки: 1 · требуется для ${codes}`]);
    expect(items.join(' ')).not.toMatch(/обов'язкова|Колонка «/);
    await shot(page, 'required-expanded-ru-light');
  } finally {
    await preference(page, 'language', 'en');
    await preference(page, 'theme', 'light');
    await editor.close();
    // Користувач лишається без ролей (видалити його API не дає); роль — після зняття (інакше 409).
    await page.request.put(`/api/v1/users/${String(created.userId)}/roles`, { data: { roleCodes: [] } });
    await page.request.delete(`/api/v1/roles/${String(created.roleId)}`);
  }
});
