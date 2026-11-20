import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { signIn } from './multiSheet';

/**
 * ✎ UAT-recalc-ux (UAT 07.11; слово людини: «тільки погоджені не мають перераховуватися … це також має логуватися»)
 * проти РЕАЛЬНОГО стенда (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep recalcSkipsApproved`):
 *  1. документ на трьох аркушах власного проєкту: аркуш A поданий, B затверджений, C чернетка;
 *  2. Documents (✎ Period clarity: раніше Periods) → «Recalculate» проєкту: замість відмови «Period … has submitted sheets» — перелік «1 затверджений аркуш
 *     пропущено» з посиланням, яке відкриває документ на аркуші B у його періоді;
 *  3. поданий A перераховано — на картці документа позначка «Recalculated after submission» для погоджувача;
 *  4. Documents → фільтр «Partly submitted» знаходить документ.
 *
 * ⛔ Без стенда файл ПАДАЄ (`globalSetup.ts`), а не пропускається (honesty-guard).
 * ⚠ Проєкт і документ — власні (`createMultiSheetDocument`), базову лінію стенда файл не чіпає.
 * ⚠ Написано без локального прогону (правило «e2e — на CI»): перший живий прогін — CI.
 */

interface DocumentTable {
  tableInstanceId: number;
  sheetDefId: number;
  sheetCode: string;
}

interface DocumentCard {
  projectId: number;
  businessKey: string;
  sheets?: { code: string; recalculatedAfterSubmission?: boolean }[] | null;
}

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

interface Slice {
  tableInstanceId: number;
  rows: { rowKey: string; rowVersion: string; cells: Record<string, unknown> }[];
}

const column = (ordinal: number, header: string, dataType: string): Record<string, unknown> => ({
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
});

/**
 * ✎ B79: позначку «перераховано після подання» ставлять лише поданому аркушу, де перерахунок ЗМІНИВ число. Тому фікстура:
 * S1 (подамо) має формулу Y = [S3].[T3].[R1].[A], що читає джерело на чернетці S3; S2 (затвердимо) — порожній.
 * Джерело S3 змінюють ПІСЛЯ подання — число S1 розходиться зі збереженим, і перерахунок його оновлює.
 */
async function createSourceDocument(page: Page): Promise<{ documentId: number; periodKey: number; projectId: number }> {
  const stamp = String(Date.now());
  const template = await ok<{ templateId: number }>(
    await page.request.post('/api/v1/templates', { data: { code: `E2ERc${stamp}`, nameL10n: { en: `E2E recalc ${stamp}` } } }),
    'шаблон',
  );
  const version = await ok<{ versionId: number }>(
    await page.request.post(`/api/v1/templates/${String(template.templateId)}/versions`, {
      data: { versionNumber: '1.0.0.0', cloneFromVersionId: null },
    }),
    'версія',
  );
  const base = `/api/v1/template-versions/${String(version.versionId)}`;
  const tableIds: number[] = [];
  for (let n = 1; n <= 3; n += 1) {
    await ok(
      await page.request.put(`${base}/sheets/S${String(n)}`, {
        data: { nameL10n: { en: `Sheet ${String(n)}` }, ordinal: n, isMandatory: true, isVisible: true, sheetGroup: null },
      }),
      `аркуш S${String(n)}`,
    );
    const table = await ok<{ id: number }>(
      await page.request.put(`${base}/sheets/S${String(n)}/tables/T${String(n)}`, {
        data: { nameL10n: { en: `Table ${String(n)}` }, ordinal: 0, layoutKind: 'PerPeriodInstance', rowMode: 'Fixed', maxDynamicRows: null },
      }),
      `таблиця T${String(n)}`,
    );
    tableIds.push(table.id);
    await ok(
      await page.request.put(`${base}/tables/${String(table.id)}/rows/R1`, {
        data: { labelL10n: { en: 'Row 1' }, ordinal: 1, rowKind: 'Item', parentRowKey: null, isReadOnly: false },
      }),
      `рядок R1 таблиці T${String(n)}`,
    );
  }
  const [t1, t2, t3] = tableIds as [number, number, number];
  await ok(await page.request.put(`${base}/tables/${String(t1)}/columns/Y`, { data: column(0, 'Y', 'Formula') }), 'колонка Y');
  await ok(await page.request.put(`${base}/tables/${String(t2)}/columns/C1`, { data: column(0, 'C1', 'String') }), 'колонка C1');
  await ok(await page.request.put(`${base}/tables/${String(t3)}/columns/A`, { data: column(0, 'A', 'Decimal') }), 'колонка A');
  const draft = await ok<{ sheets: { tables: { id: number; columns: { id: number; code: string }[] }[] }[] }>(
    await page.request.get(`${base}/structure`),
    'структура',
  );
  const yId = draft.sheets[0]?.tables[0]?.columns.find((c) => c.code === 'Y')?.id ?? 0;
  await ok(
    await page.request.put(`${base}/tables/${String(t1)}/formulas/column/${String(yId)}`, {
      data: { dialect: 'Template', expression: '[S3].[T3].[R1].[A]' },
    }),
    'формула Y',
  );
  await ok(await page.request.post(`${base}/publish`, { data: { reason: 'e2e: перерахунок поданого аркуша' } }), 'публікація');

  const policies = await ok<{ id: number; code: string }[]>(await page.request.get('/api/v1/projects/period-policies'), 'політики');
  const policy = policies.find((item) => item.code === 'ECR-Standard') ?? policies[0];
  const project = await ok<{ projectId: number }>(
    await page.request.post('/api/v1/projects', {
      data: {
        code: `E2ERcPrj${stamp}`,
        nameL10n: { en: `E2E recalc project ${stamp}` },
        periodKind: 'Monthly',
        timeZoneId: 'UTC',
        templateVersionId: version.versionId,
        periodPolicyId: policy?.id,
        year: new Date().getUTCFullYear(),
      },
    }),
    'проєкт',
  );
  await ok(await page.request.post(`/api/v1/projects/${String(project.projectId)}/activate`), 'активація');
  const structure = await ok<{ sheets: { id: number }[] }>(await page.request.get(`${base}/structure`), 'структура версії');
  const document = await ok<{ documentId: number }>(
    await page.request.post('/api/v1/documents', {
      data: { projectId: project.projectId, sheetDefIds: structure.sheets.map((x) => x.id), templateVersionId: version.versionId },
    }),
    'документ',
  );
  const calendar = await ok<{ periods: { periodKey: number; state: string }[] }>(
    await page.request.get(`/api/v1/projects/${String(project.projectId)}/periods`),
    'календар',
  );
  const periodKey = (calendar.periods.find((item) => item.state === 'Open') ?? calendar.periods[0])?.periodKey ?? 0;

  return { documentId: document.documentId, periodKey, projectId: project.projectId };
}

/** Ставить A у рядку R1 таблиці-джерела S3. */
async function setSource(page: Page, documentId: number, tableInstanceId: number, periodKey: number, value: string): Promise<void> {
  const slice = await ok<Slice>(await page.request.get(`/api/v1/documents/${String(documentId)}/tables/${String(tableInstanceId)}`), 'зріз S3');
  const row = slice.rows.find((candidate) => candidate.rowKey === 'R1');
  await ok(
    await page.request.patch(`/api/v1/documents/${String(documentId)}/cells`, {
      data: {
        tableInstanceId,
        periodKey,
        origin: 'UserEdit',
        rows: [{ rowKey: 'R1', baseVersion: row?.rowVersion, cells: [{ columnCode: 'A', value, isEmpty: false }] }],
      },
    }),
    `джерело A=${value}`,
  );
}

test.describe('Recalculation skips approved sheets and marks recalculated submitted sheets', () => {
  test.describe.configure({ mode: 'serial', timeout: 300_000 });

  test('project recalculation lists the approved sheet with a link, marks the submitted sheet and the documents filter finds a partly submitted document', async ({
    page,
  }) => {
    await signIn(page);
    const { documentId, periodKey } = await createSourceDocument(page);
    const id = String(documentId);
    const period = String(periodKey);

    // ── Аркуші документа: по таблиці на аркуш (`createMultiSheetDocument`) ──────────────────────
    const tables = await ok<DocumentTable[]>(
      await page.request.get(`/api/v1/documents/${id}/tables?periodKey=${period}`),
      'GET tables',
    );
    const sheets = [...new Map(tables.map((table) => [table.sheetDefId, table])).values()];
    expect(sheets.length, 'документ без трьох аркушів').toBeGreaterThanOrEqual(3);
    const [submitted, approved] = sheets as [DocumentTable, DocumentTable];

    // Джерело S3 = 1 → Y на S1 = 1 (збережене число), ДО подання.
    const source = tables.find((table) => table.sheetCode === 'S3');
    const reader = tables.find((table) => table.sheetCode === 'S1');
    expect(source, 'немає таблиці-джерела S3').toBeDefined();
    expect(reader, 'немає таблиці-читача S1').toBeDefined();
    await setSource(page, documentId, source?.tableInstanceId ?? 0, periodKey, '1');
    const readY = async (): Promise<number> => {
      const slice = await ok<Slice>(await page.request.get(`/api/v1/documents/${id}/tables/${String(reader?.tableInstanceId)}`), 'зріз S1');
      return Number(slice.rows[0]?.cells['Y'] ?? Number.NaN);
    };
    await expect.poll(readY, { message: 'Y на S1 не порахувалась з джерела', timeout: 60_000 }).toBe(1);

    const submit = async (sheet: DocumentTable): Promise<void> => {
      await ok(
        await page.request.post(`/api/v1/documents/${id}/submit`, {
          data: { sheetDefId: sheet.sheetDefId, periodKey: periodKey, acknowledgeWarnings: true },
        }),
        `submit ${sheet.sheetCode}`,
      );
    };

    await submit(submitted);
    await submit(approved);
    await ok(
      await page.request.post(`/api/v1/documents/${id}/approve`, {
        data: { sheetDefId: approved.sheetDefId, periodKey: periodKey, approved: true, reason: null },
      }),
      `approve ${approved.sheetCode}`,
    );

    // Джерело змінюють ПІСЛЯ подання: збережене Y=1 розходиться з обчисленим 2 — перерахунок має його змінити.
    await setSource(page, documentId, source?.tableInstanceId ?? 0, periodKey, '2');

    const card = await ok<DocumentCard>(
      await page.request.get(`/api/v1/documents/${id}?periodKey=${period}`),
      'GET document',
    );

    // ── Documents → Recalculate (✎ Period clarity: дія проєкту переїхала зі сторінки Periods на перелік документів):
    // перелік пропущених замість відмови ──────────────────────────────────────────────────────────
    await page.goto(`/?projectId=${String(card.projectId)}`);
    await page.getByRole('button', { name: /^(Recalculate|Пересчитать|Қайта есептеу)$/ }).first().click();
    await page
      .getByRole('dialog')
      .getByRole('button', { name: /^(Recalculate|Пересчитать|Қайта есептеу)$/ })
      .click();

    const skipped = page.locator('[data-recalc-skipped]');
    await expect(skipped, 'перелік пропущених аркушів не з’явився').toBeVisible({ timeout: 180_000 });
    await expect(skipped.locator('summary')).toContainText('1');
    await skipped.locator('summary').click();

    const link = skipped.locator('[data-recalc-skipped-link]');
    await expect(link).toHaveCount(1);
    await expect(link).toHaveAttribute(
      'href',
      `/documents/${id}?periodKey=${period}&sheet=${encodeURIComponent(approved.sheetCode)}`,
    );

    // ── Поданий аркуш перераховано й позначено (журнал → картка) ────────────────────────────────
    await expect
      .poll(
        async () => {
          const fresh = await ok<DocumentCard>(
            await page.request.get(`/api/v1/documents/${id}?periodKey=${period}`),
            'GET document',
          );
          return fresh.sheets?.find((sheet) => sheet.code === submitted.sheetCode)?.recalculatedAfterSubmission ?? false;
        },
        { message: 'поданий аркуш без позначки «перераховано після подання»', timeout: 180_000 },
      )
      .toBe(true);

    // Посилання з переліку відкриває документ на затвердженому аркуші в його періоді.
    await link.click();
    await expect(page).toHaveURL(new RegExp(`/documents/${id}\\?periodKey=${period}&sheet=`));

    await page.goto(`/documents/${id}?periodKey=${period}&sheet=${encodeURIComponent(submitted.sheetCode)}`);
    await expect(page.locator('[data-recalculated-after-submission]')).toBeVisible({ timeout: 60_000 });

    // ── Documents: фільтр «Partly submitted» ───────────────────────────────────────────────────
    // Перелік документів живе на `/` (routes.ts: home), не на `/documents`.
    await page.goto(`/?periodKey=${period}&projectId=${String(card.projectId)}`);
    await page.getByRole('combobox', { name: /^(State|Состояние|Күйі)$/ }).selectOption('PartlySubmitted');
    await expect(page).toHaveURL(/state=PartlySubmitted/);
    await expect(page.locator(`[data-document-row="${card.businessKey}"]`)).toBeVisible({ timeout: 60_000 });
  });
});
