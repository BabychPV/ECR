import { expect, test, type APIResponse, type BrowserContext, type Page } from '@playwright/test';

/**
 * AN-142 (Q-17, амендує D-457) на РЕАЛЬНОМУ стенді (`tools/e2e-stand.ps1`, workflow `e2e-stand`,
 * `grep=anchorTextSubmittedLive`): коли ВСІ місяці року подано (річний агрегат аркуша — `Submitted`), текст
 * якоря (звичайна `String`-колонка таблиці «місяці в колонках», не ключ і не обчислювана) править і поданий аркуш,
 * а число якоря лишається замкненим (`DocumentSubmitted`).
 *
 *  - сітка не замикається цілком (клієнт віддає рішення серверу по комірці): текстова комірка редагується,
 *    правка доходить до сервера (PATCH 200), після перезавантаження значення на місці;
 *  - числова комірка якоря в тому самому аркуші — `readOnly` із підказкою (`title` — текст каталогу, не ключ
 *    `deny.…`); спроба вставки нічого не шле; прямий PATCH — 403 `DocumentSubmitted`;
 *  - серверний зріз: текстова комірка без відмови, числова — `DocumentSubmitted`.
 *
 * ⚠ Стенд (`Collection:MonthColumnRouting=true` задає `e2e-stand.ps1`; сід-шаблон такої таблиці не має) —
 * документ збирається тут через API: шаблон `MonthsInColumns` (текст + число + 12 місячних колонок), політика з
 * довгими зсувами й проєкт ПОПЕРЕДНЬОГО року, щоб усі 12 місяців були відкриті й їх можна було подати.
 *
 * ⚠ Написано без локального прогону (на машині автора немає стенда): перший живий прогін — CI.
 */
const Admin = { userName: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const RowKey = 'ANCHOR1';
const TextHeader = 'AncText';
const NumberHeader = 'AncNum';
const NoteValue = 'anchor-note-142';

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

function column(
  ordinal: number,
  header: string,
  dataType: 'String' | 'Decimal',
  month: number | null,
): Record<string, unknown> {
  return {
    headerL10n: { en: header },
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
    unitId: null,
    ...(month === null ? {} : { isMonthColumn: true, monthNumber: month }),
  };
}

interface Arranged {
  documentId: number;
  sheetDefId: number;
  viewPeriod: number;
  tableInstanceId: number;
}

interface Slice {
  tableInstanceId: number;
  periodKey: number;
  columns: { code: string; headerL10n?: Record<string, string> }[];
  rows: { rowKey: string; rowVersion: string; cells: Record<string, unknown> }[];
  cellPermissions: Record<string, string>;
}

/** Документ «місяці в колонках» року, де ВСІ 12 місяців подано, з одним рядком якоря. */
async function arrange(context: BrowserContext): Promise<Arranged> {
  const request = context.request;
  const stamp = String(Date.now());
  const year = new Date().getUTCFullYear() - 1;

  const template = await ok<{ templateId: number }>(
    await request.post('/api/v1/templates', { data: { code: `E2EAnchor${stamp}`, nameL10n: { en: `E2E anchor ${stamp}` } } }),
    'заведення шаблону',
  );
  const version = await ok<{ versionId: number }>(
    await request.post(`/api/v1/templates/${String(template.templateId)}/versions`, {
      data: { versionNumber: '1.0.0.0', cloneFromVersionId: null },
    }),
    'заведення версії шаблону',
  );
  const base = `/api/v1/template-versions/${String(version.versionId)}`;

  await ok(
    await request.put(`${base}/sheets/S1`, {
      data: { nameL10n: { en: 'Anchor sheet' }, ordinal: 1, isMandatory: true, isVisible: true, sheetGroup: null },
    }),
    'аркуш S1',
  );
  const table = await ok<{ id: number }>(
    await request.put(`${base}/sheets/S1/tables/T1`, {
      data: { nameL10n: { en: 'Anchor table' }, ordinal: 0, layoutKind: 'MonthsInColumns', rowMode: 'Dynamic', maxDynamicRows: 50 },
    }),
    'таблиця T1 (MonthsInColumns)',
  );
  const columns: [string, Record<string, unknown>][] = [
    ['TXT', column(0, TextHeader, 'String', null)],
    ['NUM', column(1, NumberHeader, 'Decimal', null)],
    ...Array.from({ length: 12 }, (_, index): [string, Record<string, unknown>] => [
      `M${String(index + 1).padStart(2, '0')}`,
      column(index + 2, `Month ${String(index + 1)}`, 'Decimal', index + 1),
    ]),
  ];
  for (const [code, body] of columns) {
    await ok(await request.put(`${base}/tables/${String(table.id)}/columns/${code}`, { data: body }), `стовпець ${code}`);
  }
  await ok(await request.post(`${base}/publish`, { data: { reason: 'e2e: текст якоря поданого року' } }), 'публікація версії');

  // Політика з довгими зсувами: усі місяці минулого року лишаються відкритими, тож їх можна подати.
  const policy = await ok<{ id: number }>(
    await request.post('/api/v1/projects/period-policies', {
      data: { code: `E2EAnc${stamp}`, openOffsetDays: 0, graceOffsetDays: 3000, hardCloseOffsetDays: 3600, yearGraceOffsetDays: 3600 },
    }),
    'політика періодів',
  );
  const project = await ok<{ projectId: number }>(
    await request.post('/api/v1/projects', {
      data: {
        code: `E2EAncPrj${stamp}`,
        nameL10n: { en: `E2E anchor project ${stamp}` },
        periodKind: 'Monthly',
        timeZoneId: 'UTC',
        templateVersionId: version.versionId,
        periodPolicyId: policy.id,
        year,
      },
    }),
    'проєкт',
  );
  await ok(await request.post(`/api/v1/projects/${String(project.projectId)}/activate`), 'активація проєкту');

  const calendar = await ok<{ periods: { periodKey: number; state: string }[] }>(
    await request.get(`/api/v1/projects/${String(project.projectId)}/periods`),
    'календар проєкту',
  );
  expect(calendar.periods.length, 'у календарі року не 12 місяців').toBe(12);
  for (const period of calendar.periods) {
    expect(['Open', 'Grace'], `період ${String(period.periodKey)} не відкритий (${period.state}) — подати не вийде`).toContain(
      period.state,
    );
  }

  const structure = await ok<{ sheets: { id: number }[] }>(await request.get(`${base}/structure`), 'структура версії');
  const sheetDefId = structure.sheets[0]?.id ?? 0;
  expect(sheetDefId, 'у версії немає аркуша').toBeGreaterThan(0);
  const document = await ok<{ documentId: number }>(
    await request.post('/api/v1/documents', {
      data: { projectId: project.projectId, sheetDefIds: [sheetDefId], templateVersionId: version.versionId },
    }),
    'документ',
  );

  // Усі 12 екземплярів таблиці (прапор маршрутизації дзеркалить рядки якоря в наявні).
  const periodKeys = Array.from({ length: 12 }, (_, index) => year * 100 + index + 1);
  const instanceIds = new Map<number, number>();
  for (const periodKey of periodKeys) {
    const tables = await ok<{ tableInstanceId: number }[]>(
      await request.get(`/api/v1/documents/${String(document.documentId)}/tables?periodKey=${String(periodKey)}`),
      `таблиці ${String(periodKey)}`,
    );
    const instance = tables[0];
    if (instance === undefined) throw new Error(`у документа немає таблиць за ${String(periodKey)}`);
    instanceIds.set(periodKey, instance.tableInstanceId);
  }

  const viewPeriod = year * 100 + 10;
  const viewInstance = instanceIds.get(viewPeriod) ?? 0;
  await ok(
    await request.post(`/api/v1/documents/${String(document.documentId)}/rows`, {
      data: { tableInstanceId: viewInstance, rowKey: RowKey },
    }),
    'рядок якоря',
  );

  // Подання всіх місяців по порядку → річний агрегат аркуша — Submitted.
  for (const periodKey of periodKeys) {
    const submit = await request.post(`/api/v1/documents/${String(document.documentId)}/submit`, {
      data: { sheetDefId, periodKey, acknowledgeWarnings: true },
    });
    expect(submit.status(), `submit ${String(periodKey)}: ${await submit.text()}`).toBe(204);
  }

  return { documentId: document.documentId, sheetDefId, viewPeriod, tableInstanceId: viewInstance };
}

async function sliceOf(context: BrowserContext, arranged: Arranged): Promise<Slice> {
  const response = await context.request.get(
    `/api/v1/documents/${String(arranged.documentId)}/tables/${String(arranged.tableInstanceId)}`,
  );
  expect(response.ok(), `GET slice: ${String(response.status())} ${await response.text()}`).toBe(true);

  return (await response.json()) as Slice;
}

/** Індекс колонки в сітці за текстом її заголовка (RevoGrid; ліва колонка підписів зсуває нумерацію). */
async function gridColumn(page: Page, header: string): Promise<string> {
  const cell = page.locator('revo-grid revogr-header .rgHeaderCell', { hasText: header }).first();
  await expect(cell, `заголовка «${header}» немає в сітці`).toBeVisible({ timeout: 60_000 });
  const index = await cell.getAttribute('data-rgcol');
  if (index === null) throw new Error(`заголовок «${header}» без data-rgcol`);

  return index;
}

function dataCell(page: Page, index: string) {
  return page.locator(`revo-grid revogr-data[type="rgRow"] .rgCell[data-rgrow="0"][data-rgcol="${index}"]`);
}

test.describe('anchorTextSubmitted: текст якоря поданого року править, число — ні (AN-142)', () => {
  test.describe.configure({ mode: 'serial', timeout: 420_000 });

  test('текстова комірка якоря редагується в поданому аркуші й зберігається; числова — замкнена з підказкою', async ({
    browser,
  }) => {
    const context = await browser.newContext({ permissions: ['clipboard-read', 'clipboard-write'] });
    try {
      const login = await context.request.post('/api/v1/login/local', {
        data: { userName: Admin.userName, password: Admin.password },
      });
      expect(login.ok(), `вхід ${Admin.userName}: ${String(login.status())} ${await login.text()}`).toBe(true);

      const arranged = await arrange(context);

      // ── 1. Серверний зріз: текст якоря без відмови, число — DocumentSubmitted ─────────────────────────
      const slice = await sliceOf(context, arranged);
      const row = slice.rows.find((candidate) => candidate.rowKey === RowKey);
      if (row === undefined) throw new Error(`у зрізі немає рядка ${RowKey}`);
      expect(slice.cellPermissions[`${RowKey}:TXT`], 'текст якоря має бути редаговним').toBeUndefined();
      expect(slice.cellPermissions[`${RowKey}:NUM`]).toBe('DocumentSubmitted');

      // ── 2. Прямий PATCH числа якоря — 403 DocumentSubmitted ───────────────────────────────────────────
      const refused = await context.request.patch(`/api/v1/documents/${String(arranged.documentId)}/cells`, {
        data: {
          tableInstanceId: slice.tableInstanceId,
          periodKey: slice.periodKey,
          origin: 'UserEdit',
          rows: [{ rowKey: RowKey, baseVersion: row.rowVersion, cells: [{ columnCode: 'NUM', value: 7 }] }],
        },
      });
      expect(refused.status(), `PATCH числа якоря: ${await refused.text()}`).toBe(403);
      expect(JSON.stringify(await refused.json())).toContain('DocumentSubmitted');

      // ── 3. Сітка: текст якоря редагується, число — «лише читання» з підказкою ─────────────────────────
      const page = await context.newPage();
      const writes: string[] = [];
      page.on('request', (request) => {
        if (request.method() === 'PATCH' && new URL(request.url()).pathname.endsWith('/cells')) {
          writes.push(request.postData() ?? '');
        }
      });
      await page.goto(`/documents/${String(arranged.documentId)}?periodKey=${String(arranged.viewPeriod)}`);
      await expect(page.getByRole('tab', { selected: true })).toContainText('Submitted', { timeout: 60_000 });

      const textColumn = await gridColumn(page, TextHeader);
      const numberColumn = await gridColumn(page, NumberHeader);
      const textCell = dataCell(page, textColumn);
      const numberCell = dataCell(page, numberColumn);
      await expect(textCell, 'комірки тексту якоря немає в сітці').toBeVisible({ timeout: 60_000 });

      await expect(numberCell).toHaveAttribute('data-cell-state', 'readOnly');
      const hint = (await numberCell.getAttribute('title')) ?? '';
      expect(hint.trim().length, 'у замкненої числової комірки немає підказки').toBeGreaterThan(0);
      expect(hint, 'підказка — ключ каталогу, а не текст').not.toMatch(/deny\./);
      await expect(textCell, 'текст якоря в поданому аркуші показано «лише читання»').not.toHaveAttribute(
        'data-cell-state',
        'readOnly',
      );

      // Число: вставка нічого не шле.
      await numberCell.click();
      await page.evaluate(async (value) => {
        await navigator.clipboard.writeText(value);
      }, '5');
      await page.keyboard.press('Control+v');
      await page.keyboard.press('Control+s');
      await page.waitForTimeout(2_000);
      expect(writes, 'вставка в замкнену числову комірку якоря дійшла до сервера').toEqual([]);

      // Повідомлення про замок: діалог «відхилено» називає комірку й ПРИЧИНУ текстом каталогу (не ключем).
      const rejected = page.getByRole('dialog');
      await expect(rejected, 'вставка в замкнену комірку не показала повідомлення про замок').toBeVisible({ timeout: 15_000 });
      const rejectedText = (await rejected.innerText()).replace(/\s+/g, ' ');
      expect(rejectedText).toContain(RowKey);
      expect(rejectedText).toContain('NUM');
      expect(rejectedText, 'у повідомленні про замок — ключ каталогу, а не текст').not.toMatch(/deny\.|err\./);
      expect(hint.length, 'підказка комірки порожня').toBeGreaterThan(0);
      console.log(`[anchorTextSubmitted] повідомлення про замок: ${rejectedText}`);
      await page.keyboard.press('Escape');
      await expect(rejected).toBeHidden({ timeout: 15_000 });

      // Текст: вставка доходить до сервера, PATCH 200.
      await textCell.click();
      await page.evaluate(async (value) => {
        await navigator.clipboard.writeText(value);
      }, NoteValue);
      const saved = page.waitForResponse(
        (response) =>
          response.request().method() === 'PATCH' &&
          new URL(response.url()).pathname === `/api/v1/documents/${String(arranged.documentId)}/cells`,
        { timeout: 90_000 },
      );
      await page.keyboard.press('Control+v');
      await page.keyboard.press('Control+s');
      const savedResponse = await saved;
      expect(savedResponse.status(), `PATCH тексту якоря: ${await savedResponse.text()}`).toBe(200);

      // ── 4. Після перезавантаження значення на місці; число не змінилось ───────────────────────────────
      await page.reload();
      await expect(page.getByRole('tab', { selected: true })).toContainText('Submitted', { timeout: 60_000 });
      const reloadedText = dataCell(page, await gridColumn(page, TextHeader));
      await expect(reloadedText).toContainText(NoteValue, { timeout: 60_000 });

      const after = await sliceOf(context, arranged);
      const stored = after.rows.find((candidate) => candidate.rowKey === RowKey);
      expect(String(stored?.cells['TXT'] ?? '')).toBe(NoteValue);
      expect(stored?.cells['NUM'] ?? null).toBeNull();

      await page.close();
    } finally {
      await context.close();
    }
  });
});
