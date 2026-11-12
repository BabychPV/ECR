import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { signIn } from './multiSheet';

/**
 * ✎ C-1 (D-475, рішення PO 11.11) на живому стенді (`tools/e2e-stand.ps1`, workflow `e2e-stand`,
 * `-Grep "formula errors"`): помилка формули НЕ перезаписує значення і не перераховує залежні.
 *
 * Шаблон — одна динамічна таблиця: входи A, B, E і формули C = [A]/[B], D = [C]*2 + [E]. Рядок: A=10, B=2, E=1 →
 * C=5, D=11. Правка B=0, E=2:
 *   — сервер: C і D лишаються 5 і 11; зріз має `calcErrors` (C — Error #DIV/0, D — Blocked від C); панель проблем —
 *     група `formulaErrors` з формулою `[A] / [B]` і входами; журнал — одна подія `CalcError`; «Подати» — 422
 *     `ECR-SUB-4221` з `FORMULA-ERROR`;
 *   — клієнт: позначки комірок (`data-calc-error`) і банер «Formula errors» з переходом до комірки;
 *   — B=2: D=12 записано, позначок і групи немає, у журналі — `CalcErrorResolved`, подання проходить.
 *
 * ⛔ Без стенда файл ПАДАЄ (вхід/запити до API), а не пропускається (honesty-guard).
 */
test.describe.configure({ timeout: 300_000 });

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
    precision: 18,
    scale: 2,
    defaultValue: null,
    displayFormat: null,
    lookupFilter: null,
    lookupRegistryDefId: null,
    styleId: null,
    unitId: null,
  };
}

interface Slice {
  rows: { rowKey: string; rowVersion: string; cells: Record<string, unknown> }[];
  calcErrors?: Record<string, { kind: string; errorCode: string; source: { columnCode: string } | null }> | null;
}

interface Arranged {
  documentId: number;
  periodKey: number;
  sheetDefId: number;
  tableInstanceId: number;
  rowKey: string;
}

const asNumber = (value: unknown): number => (value === null || value === undefined ? Number.NaN : Number(value));

/** Журнал змін комірок документа за походженням (вікно — доба назад і вперед). */
function cellChangesUrl(documentId: number, origin: string): string {
  const day = 24 * 60 * 60 * 1000;
  const query = new URLSearchParams({
    from: new Date(Date.now() - day).toISOString(),
    to: new Date(Date.now() + day).toISOString(),
    limit: '100',
    documentId: String(documentId),
    origin,
  });

  return `/api/v1/audit/cells?${query.toString()}`;
}

async function arrange(page: Page): Promise<Arranged> {
  const stamp = String(Date.now());
  const template = await ok<{ templateId: number }>(
    await page.request.post('/api/v1/templates', { data: { code: `E2EFe${stamp}`, nameL10n: { en: `E2E formula errors ${stamp}` } } }),
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
      data: { nameL10n: { en: 'Ratios' }, ordinal: 0, layoutKind: 'Static', rowMode: 'Dynamic', maxDynamicRows: 50 },
    }),
    'таблиця',
  );
  for (const [index, [code, dataType]] of (
    [
      ['A', 'Decimal'],
      ['B', 'Decimal'],
      ['E', 'Decimal'],
      ['C', 'Formula'],
      ['D', 'Formula'],
    ] as const
  ).entries()) {
    await ok(
      await page.request.put(`${base}/tables/${String(table.id)}/columns/${code}`, { data: column(index, code, dataType) }),
      `колонка ${code}`,
    );
  }
  const draft = await ok<{ sheets: { tables: { id: number; columns: { id: number; code: string }[] }[] }[] }>(
    await page.request.get(`${base}/structure`),
    'структура чернетки',
  );
  const idOf = (code: string): number =>
    draft.sheets[0]?.tables[0]?.columns.find((candidate) => candidate.code === code)?.id ?? 0;
  await ok(
    await page.request.put(`${base}/tables/${String(table.id)}/formulas/column/${String(idOf('C'))}`, {
      data: { dialect: 'Template', expression: '[A] / [B]' },
    }),
    'формула C',
  );
  await ok(
    await page.request.put(`${base}/tables/${String(table.id)}/formulas/column/${String(idOf('D'))}`, {
      data: { dialect: 'Template', expression: '[C] * 2 + [E]' },
    }),
    'формула D',
  );
  await ok(await page.request.post(`${base}/publish`, { data: { reason: 'e2e: C-1 помилка формули' } }), 'публікація');

  const policies = await ok<{ id: number; code: string }[]>(await page.request.get('/api/v1/projects/period-policies'), 'політики');
  const policy = policies.find((item) => item.code === 'ECR-Standard') ?? policies[0];
  const project = await ok<{ projectId: number }>(
    await page.request.post('/api/v1/projects', {
      data: {
        code: `E2EFePrj${stamp}`,
        nameL10n: { en: `E2E formula errors ${stamp}` },
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
  const sheetDefId = structure.sheets[0]?.id ?? 0;
  const document = await ok<{ documentId: number }>(
    await page.request.post('/api/v1/documents', {
      data: { projectId: project.projectId, sheetDefIds: [sheetDefId], templateVersionId: version.versionId },
    }),
    'документ',
  );
  const calendar = await ok<{ periods: { periodKey: number; state: string }[] }>(
    await page.request.get(`/api/v1/projects/${String(project.projectId)}/periods`),
    'календар',
  );
  const periodKey = (calendar.periods.find((item) => item.state === 'Open') ?? calendar.periods[0])?.periodKey ?? 0;
  const documentUrl = `/api/v1/documents/${String(document.documentId)}`;
  const tables = await ok<{ tableInstanceId: number }[]>(
    await page.request.get(`${documentUrl}/tables?periodKey=${String(periodKey)}`),
    'таблиці документа',
  );
  const tableInstanceId = tables[0]?.tableInstanceId ?? 0;
  await ok(await page.request.post(`${documentUrl}/rows`, { data: { tableInstanceId, rowKey: null } }), 'рядок');
  const slice = await ok<Slice>(await page.request.get(`${documentUrl}/tables/${String(tableInstanceId)}`), 'зріз');
  const row = slice.rows[0];
  expect(row, 'рядок таблиці').toBeDefined();

  return { documentId: document.documentId, periodKey, sheetDefId, tableInstanceId, rowKey: row?.rowKey ?? '' };
}

async function sliceOf(page: Page, arranged: Arranged): Promise<Slice> {
  return ok<Slice>(
    await page.request.get(`/api/v1/documents/${String(arranged.documentId)}/tables/${String(arranged.tableInstanceId)}`),
    'зріз',
  );
}

/** Пише входи рядка і чекає, поки фоновий перерахунок дасть очікуваний стан зрізу. */
async function patch(page: Page, arranged: Arranged, cells: Record<string, string>): Promise<void> {
  const current = await sliceOf(page, arranged);
  const row = current.rows.find((candidate) => candidate.rowKey === arranged.rowKey);
  await ok(
    await page.request.patch(`/api/v1/documents/${String(arranged.documentId)}/cells`, {
      data: {
        tableInstanceId: arranged.tableInstanceId,
        periodKey: arranged.periodKey,
        origin: 'UserEdit',
        rows: [
          {
            rowKey: arranged.rowKey,
            baseVersion: row?.rowVersion,
            cells: Object.entries(cells).map(([columnCode, value]) => ({ columnCode, value, isEmpty: false })),
          },
        ],
      },
    }),
    'значення рядка',
  );
}

async function valuesOf(page: Page, arranged: Arranged): Promise<{ c: number; d: number; errors: Slice['calcErrors'] }> {
  const slice = await sliceOf(page, arranged);
  const row = slice.rows.find((candidate) => candidate.rowKey === arranged.rowKey);

  return { c: asNumber(row?.cells['C']), d: asNumber(row?.cells['D']), errors: slice.calcErrors ?? null };
}

test('C-1 formula errors (помилка формули): value kept, dependents not recalculated, marks, issues group, audit, submit blocked', async ({ page }) => {
  await signIn(page);
  await page.setViewportSize({ width: 1440, height: 900 });
  const arranged = await arrange(page);
  const documentUrl = `/api/v1/documents/${String(arranged.documentId)}`;

  // 1. Добрі входи: C = 5, D = 11.
  await patch(page, arranged, { A: '10', B: '2', E: '1' });
  await expect
    .poll(async () => (await valuesOf(page, arranged)).d, { timeout: 120_000, message: 'D = [C]*2 + [E] не порахувалась' })
    .toBe(11);

  // 2. B = 0, E = 2: C — помилка, D — не оновлена; значення лишаються останні правильні.
  await patch(page, arranged, { B: '0', E: '2' });
  await expect
    .poll(async () => Object.keys((await valuesOf(page, arranged)).errors ?? {}).length, {
      timeout: 120_000,
      message: 'зріз не отримав позначок помилки формули',
    })
    .toBe(2);
  const broken = await valuesOf(page, arranged);
  expect(broken.c, 'C лишається останнім правильним').toBe(5);
  expect(broken.d, 'D не перераховано від помилки (без C-1 було б 12)').toBe(11);
  expect(broken.errors?.[`${arranged.rowKey}:C`]).toMatchObject({ kind: 'Error', errorCode: '#DIV/0' });
  expect(broken.errors?.[`${arranged.rowKey}:D`]).toMatchObject({ kind: 'Blocked', source: { columnCode: 'C' } });

  const issuesUrl = `${documentUrl}/calculation-issues?periodKey=${String(arranged.periodKey)}`;
  const issues = await ok<{ formulaErrors: { columnCode: string; kind: string; formula: string | null; inputs: { columnCode: string; value: string | null }[] | null }[] | null }>(
    await page.request.get(issuesUrl),
    'проблеми розрахунку',
  );
  const own = issues.formulaErrors?.find((error) => error.columnCode === 'C');
  expect(own?.formula).toBe('[A] / [B]');
  expect(own?.inputs?.map((input) => `${input.columnCode}=${String(input.value)}`)).toEqual(
    expect.arrayContaining(['A=10', 'B=0']),
  );

  const audit = await ok<{ items: { origin: string; reason: string | null }[] }>(
    await page.request.get(cellChangesUrl(arranged.documentId, 'CalcError')),
    'журнал змін комірок',
  );
  expect(audit.items, 'одна подія «Помилка розрахунку»').toHaveLength(1);

  const blocked = await page.request.post(`${documentUrl}/submit`, {
    data: { sheetDefId: arranged.sheetDefId, periodKey: arranged.periodKey, acknowledgeWarnings: true },
  });
  const blockedBody = await blocked.text();
  expect(blocked.status(), blockedBody).toBe(422);
  expect(blockedBody).toContain('ECR-SUB-4221');
  expect(blockedBody).toContain('FORMULA-ERROR');

  // 3. Клієнт: позначки комірок і банер з переходом до комірки.
  await page.goto(`/documents/${String(arranged.documentId)}?periodKey=${String(arranged.periodKey)}`);
  const banner = page.getByTestId('document-formula-errors');
  await expect(banner).toBeVisible({ timeout: 60_000 });
  await expect(banner).toContainText('Formula errors');
  await expect(page.getByTestId('document-formula-error')).toHaveCount(2);
  await expect(page.locator('[data-calc-error="Error"]')).toHaveCount(1, { timeout: 60_000 });
  await expect(page.locator('[data-calc-error="Blocked"]')).toHaveCount(1);
  await page.getByTestId('document-formula-error-open').first().click();
  await expect(page.locator('.ecr-cell-nav-target'), 'підсвічена комірка').toHaveCount(1, { timeout: 30_000 });

  // 4. Виправлення: B = 2 → D = 5*2 + 2 = 12, позначки й група зникають, у журналі — усунення, подання проходить.
  await patch(page, arranged, { B: '2' });
  await expect
    .poll(async () => (await valuesOf(page, arranged)).d, { timeout: 120_000, message: 'D не перерахувалась після виправлення' })
    .toBe(12);
  expect((await valuesOf(page, arranged)).errors ?? null).toBeNull();
  const cleared = await ok<{ formulaErrors: unknown[] | null }>(await page.request.get(issuesUrl), 'проблеми після виправлення');
  expect(cleared.formulaErrors ?? null).toBeNull();
  const resolved = await ok<{ items: unknown[] }>(
    await page.request.get(cellChangesUrl(arranged.documentId, 'CalcErrorResolved')),
    'журнал усунення',
  );
  expect(resolved.items).toHaveLength(1);
  const submit = await page.request.post(`${documentUrl}/submit`, {
    data: { sheetDefId: arranged.sheetDefId, periodKey: arranged.periodKey, acknowledgeWarnings: true },
  });
  expect(submit.status(), `submit: ${await submit.text()}`).toBe(204);
});
