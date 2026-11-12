import { inflateRawSync, crc32 } from 'node:zlib';
import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { signIn } from './multiSheet';

/**
 * P1 12.11 (Аудит-2, Land doc 96): свіжий експорт, імпортований БЕЗ правок, відмовлявся цілком
 * (`ECR-CELL-4221 importCalculatedStale` → apply 422 `importHasRejected`). Причина — формула з ТЕКСТОВИМ результатом,
 * що виглядає числом (Land `Land_Measure_OperatingTime` над String-колонкою місяця, `Category1` = REGFIELD «6975»):
 * експорт пише текст, імпорт читав його числом і порівнював із рядком.
 *
 * Живий стенд: шаблон із колонками вводу A (Decimal), S (String) і формулами F1 = [A]*2 та T — текстовий міст над S
 * через `[Period].Sequence` (як у Land; у книгу такий вираз не транслюється — комірка лишається значенням).
 * (1) експорт → перегляд імпорту без правок: 0 відмов і 0 змін; (2) W-1: правка входу A в книзі → імпорт через діалог →
 * F1 у сітці оновлюється без F9 і без перезавантаження.
 *
 * Без стенда — пропуск. Стенд: `tools/e2e-stand.ps1`.
 */
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

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
    isReadOnly: dataType === 'Formula',
    isHidden: false,
    precision: dataType === 'String' ? null : 18,
    scale: dataType === 'String' ? null : 2,
    defaultValue: null,
    displayFormat: null,
    lookupFilter: null,
    lookupRegistryDefId: null,
    styleId: null,
    unitId: null,
  };
}

interface Slice {
  columns: { code: string }[];
  rows: { rowKey: string; rowVersion: string; label: string | null; cells: Record<string, unknown> }[];
}

interface Arranged {
  documentId: number;
  periodKey: number;
  tableInstanceId: number;
}

const sliceOf = async (page: Page, a: Arranged): Promise<Slice> =>
  ok<Slice>(await page.request.get(`/api/v1/documents/${String(a.documentId)}/tables/${String(a.tableInstanceId)}`), 'зріз');

async function arrange(page: Page): Promise<Arranged> {
  const stamp = String(Date.now());
  const template = await ok<{ templateId: number }>(
    await page.request.post('/api/v1/templates', { data: { code: `E2EXr${stamp}`, nameL10n: { en: `E2E xlsx round trip ${stamp}` } } }),
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
      data: { nameL10n: { en: 'Readings' }, ordinal: 0, layoutKind: 'Static', rowMode: 'Dynamic', maxDynamicRows: 50 },
    }),
    'таблиця',
  );
  const columns: [string, string][] = [
    ['A', 'Decimal'],
    ['S', 'String'],
    ['F1', 'Formula'],
    ['T', 'Formula'],
  ];
  for (const [index, [code, dataType]] of columns.entries()) {
    await ok(
      await page.request.put(`${base}/tables/${String(table.id)}/columns/${code}`, { data: column(index, code, dataType) }),
      `колонка ${code}`,
    );
  }

  const draft = await ok<{ sheets: { tables: { columns: { id: number; code: string }[] }[] }[] }>(
    await page.request.get(`${base}/structure`),
    'структура чернетки',
  );
  const idOf = (code: string): number => draft.sheets[0]?.tables[0]?.columns.find((c) => c.code === code)?.id ?? 0;
  await ok(
    await page.request.put(`${base}/tables/${String(table.id)}/formulas/column/${String(idOf('F1'))}`, {
      data: { dialect: 'Template', expression: '[A]*2' },
    }),
    'формула F1',
  );
  // Як Land `Land_Measure_OperatingTime`: текстовий міст над String-колонкою через `[Period].Sequence`.
  await ok(
    await page.request.put(`${base}/tables/${String(table.id)}/formulas/column/${String(idOf('T'))}`, {
      data: { dialect: 'Template', expression: '[Period].Sequence > 0 ? [S] : [S]' },
    }),
    'формула T',
  );
  await ok(await page.request.post(`${base}/publish`, { data: { reason: 'e2e: Excel round trip' } }), 'публікація');

  const policies = await ok<{ id: number; code: string }[]>(await page.request.get('/api/v1/projects/period-policies'), 'політики');
  const policy = policies.find((item) => item.code === 'ECR-Standard') ?? policies[0];
  const project = await ok<{ projectId: number }>(
    await page.request.post('/api/v1/projects', {
      data: {
        code: `E2EXrPrj${stamp}`,
        nameL10n: { en: `E2E xlsx round trip ${stamp}` },
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
  const structure = await ok<{ sheets: { id: number }[] }>(await page.request.get(`${base}/structure`), 'структура');
  const document = await ok<{ documentId: number }>(
    await page.request.post('/api/v1/documents', {
      data: { projectId: project.projectId, sheetDefIds: [structure.sheets[0]?.id ?? 0], templateVersionId: version.versionId },
    }),
    'документ',
  );
  const calendar = await ok<{ periods: { periodKey: number; state: string }[] }>(
    await page.request.get(`/api/v1/projects/${String(project.projectId)}/periods`),
    'календар',
  );
  const periodKey = (calendar.periods.find((item) => item.state === 'Open') ?? calendar.periods[0])?.periodKey ?? 0;
  const tables = await ok<{ tableInstanceId: number }[]>(
    await page.request.get(`/api/v1/documents/${String(document.documentId)}/tables?periodKey=${String(periodKey)}`),
    'таблиці документа',
  );
  const arranged: Arranged = { documentId: document.documentId, periodKey, tableInstanceId: tables[0]?.tableInstanceId ?? 0 };

  for (let n = 1; n <= 2; n += 1) {
    await ok(
      await page.request.post(`/api/v1/documents/${String(arranged.documentId)}/rows`, {
        data: { tableInstanceId: arranged.tableInstanceId, rowKey: null },
      }),
      `рядок ${String(n)}`,
    );
  }

  const slice = await sliceOf(page, arranged);
  const values: [string, string][] = [
    ['1234.5', '720'],
    ['2', '6975'],
  ];
  await ok(
    await page.request.patch(`/api/v1/documents/${String(arranged.documentId)}/cells`, {
      data: {
        tableInstanceId: arranged.tableInstanceId,
        periodKey,
        origin: 'UserEdit',
        rows: slice.rows.map((row, index) => ({
          rowKey: row.rowKey,
          baseVersion: row.rowVersion,
          cells: [
            { columnCode: 'A', value: values[index]?.[0] ?? '0', isEmpty: false },
            { columnCode: 'S', value: values[index]?.[1] ?? '', isEmpty: false },
          ],
        })),
      },
    }),
    'значення A/S',
  );

  // Перерахунок асинхронний: F1 = A*2, T = S (текстом).
  await expect
    .poll(async () => (await sliceOf(page, arranged)).rows.map((row) => [Number(row.cells['F1']), String(row.cells['T'] ?? '')]), {
      timeout: 60_000,
    })
    .toEqual([
      [2469, '720'],
      [4, '6975'],
    ]);

  return arranged;
}

async function xsrf(page: Page): Promise<Record<string, string>> {
  const token = (await page.context().cookies()).find((c) => c.name === 'XSRF-TOKEN')?.value;
  return token === undefined ? {} : { 'X-XSRF-TOKEN': decodeURIComponent(token) };
}

async function exportWorkbook(page: Page, a: Arranged): Promise<Buffer> {
  const started = await ok<{ jobId: string }>(
    await page.request.post(`/api/v1/documents/${String(a.documentId)}/export`, {
      data: { includeFormulas: true, includeStyles: true, language: 'en', periodKey: a.periodKey },
      headers: await xsrf(page),
    }),
    'експорт',
  );
  let resultUrl = '';
  await expect
    .poll(async () => {
      const job = await ok<{ state?: string; resultUrl?: string }>(
        await page.request.get(`/api/v1/jobs/${encodeURIComponent(started.jobId)}`),
        'задача експорту',
      );
      resultUrl = job.resultUrl ?? '';
      return job.state;
    }, { timeout: 120_000 })
    .toBe('Succeeded');

  const file = await page.request.get(resultUrl);
  expect(file.status()).toBe(200);
  return file.body();
}

interface Preview {
  changes: { rowKey: string; columnCode: string }[];
  rejected: { rowKey: string; columnCode: string; reasonCode: string; messageKey?: string | null }[];
}

async function preview(page: Page, a: Arranged, book: Buffer): Promise<Preview> {
  return ok<Preview>(
    await page.request.post(`/api/v1/documents/${String(a.documentId)}/import/preview`, {
      multipart: {
        file: { name: 'book.xlsx', mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', buffer: book },
        periodKey: String(a.periodKey),
      },
      headers: await xsrf(page),
    }),
    'перегляд імпорту',
  );
}

/** Частини .xlsx (zip): ім'я → вміст. Лише «stored» і «deflate» — ClosedXML інших не пише. */
function unzip(book: Buffer): Map<string, Buffer> {
  const end = book.lastIndexOf(Buffer.from([0x50, 0x4b, 0x05, 0x06]));
  const count = book.readUInt16LE(end + 10);
  let at = book.readUInt32LE(end + 16);
  const parts = new Map<string, Buffer>();
  for (let i = 0; i < count; i += 1) {
    const method = book.readUInt16LE(at + 10);
    const size = book.readUInt32LE(at + 20);
    const nameLength = book.readUInt16LE(at + 28);
    const skip = nameLength + book.readUInt16LE(at + 30) + book.readUInt16LE(at + 32);
    const local = book.readUInt32LE(at + 42);
    const name = book.toString('utf8', at + 46, at + 46 + nameLength);
    const start = local + 30 + book.readUInt16LE(local + 26) + book.readUInt16LE(local + 28);
    const raw = book.subarray(start, start + size);
    parts.set(name, method === 8 ? inflateRawSync(raw) : Buffer.from(raw));
    at += 46 + skip;
  }
  return parts;
}

/** Zip «stored» з тих самих частин. */
function zip(parts: Map<string, Buffer>): Buffer {
  const chunks: Buffer[] = [];
  const central: Buffer[] = [];
  let offset = 0;
  for (const [file, data] of parts) {
    const name = Buffer.from(file, 'utf8');
    const crc = crc32(data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(0x0800, 6);
    local.writeUInt32LE(0x00210000, 10);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(data.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(name.length, 26);
    const entry = Buffer.alloc(46);
    entry.writeUInt32LE(0x02014b50, 0);
    entry.writeUInt16LE(20, 4);
    entry.writeUInt16LE(20, 6);
    entry.writeUInt16LE(0x0800, 8);
    entry.writeUInt32LE(0x00210000, 12);
    entry.writeUInt32LE(crc, 16);
    entry.writeUInt32LE(data.length, 20);
    entry.writeUInt32LE(data.length, 24);
    entry.writeUInt16LE(name.length, 28);
    entry.writeUInt32LE(offset, 42);
    chunks.push(local, name, data);
    central.push(entry, name);
    offset += 30 + name.length + data.length;
  }
  const directory = Buffer.concat(central);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(parts.size, 8);
  end.writeUInt16LE(parts.size, 10);
  end.writeUInt32LE(directory.length, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...chunks, directory, end]);
}

/**
 * Міняє число в комірках аркушів книги (рівно одна заміна). ⚠ OpenXML SDK пише елементи з префіксом простору імен
 * (`<x:v>`), тож шукається `<v>` з будь-яким префіксом.
 */
function replaceNumber(book: Buffer, from: string, to: string): Buffer {
  const parts = unzip(book);
  const pattern = /<((?:\w+:)?v)>([^<]*)<\/\1>/g;
  let replaced = 0;
  for (const [name, data] of parts) {
    if (!name.startsWith('xl/worksheets/')) continue;
    const xml = data.toString('utf8');
    const next = xml.replace(pattern, (match, tag: string, value: string) => {
      if (value.trim() === '' || Number(value) !== Number(from)) return match;
      replaced += 1;
      return `<${tag}>${to}</${tag}>`;
    });
    if (next !== xml) parts.set(name, Buffer.from(next, 'utf8'));
  }
  const sheets = [...parts.keys()].filter((name) => name.startsWith('xl/worksheets/'));
  const sample = sheets.map((name) => (parts.get(name)?.toString('utf8').match(/<(?:\w+:)?c [^>]*>.{0,120}/g) ?? []).slice(0, 6).join(' '));
  expect(replaced, `число ${from} у книзі; аркуші ${sheets.join(', ')}; ${sample.join(' | ').slice(0, 1500)}`).toBe(1);
  return zip(parts);
}

const cellAt = (page: Page, col: number, row: number) =>
  page.locator(`revo-grid revogr-data[type="rgRow"] .rgCell[data-rgcol="${String(col)}"][data-rgrow="${String(row)}"]`);

function columnIndex(slice: Slice, code: string): number {
  const offset = slice.rows.some((row) => row.label !== null && row.label.length > 0) ? 1 : 0;
  const index = slice.columns.findIndex((candidate) => candidate.code === code);
  expect(index, `колонка ${code}`).toBeGreaterThanOrEqual(0);
  return index + offset;
}

/** Число в комірці сітки: формат показу має роздільник тисяч («2,469.00», «2 469,00») — він допускається. */
const shows = (value: number): RegExp =>
  new RegExp(`^\\s*${String(value).replace(/\B(?=(\d{3})+(?!\d))/g, '[,\\s\\u00a0\\u202f]?')}([.,]0+)?\\s*$`);

test.describe('Excel import round trip', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.');

  test('excelImportRoundTrip: unchanged export re-imports with 0 rejected and 0 changes', async ({ page }) => {
    test.slow();
    await signIn(page);
    const arranged = await arrange(page);

    const result = await preview(page, arranged, await exportWorkbook(page, arranged));

    // ⛔ До фіксу: T (текст «720»/«6975» у формульній колонці) → ECR-CELL-4221 importCalculatedStale на кожному рядку.
    expect(result.rejected, JSON.stringify(result.rejected)).toEqual([]);
    expect(result.changes, JSON.stringify(result.changes)).toEqual([]);
  });

  test('excelImportRoundTrip W-1: changed input via the import dialog updates the computed column without F9', async ({ page }) => {
    test.slow();
    await signIn(page);
    const arranged = await arrange(page);
    const book = replaceNumber(await exportWorkbook(page, arranged), '1234.5', '1500.5');

    await page.goto(`/documents/${String(arranged.documentId)}?periodKey=${String(arranged.periodKey)}`);
    await expect(page.locator('revo-grid:visible').first()).toBeVisible({ timeout: 60_000 });
    const colF1 = columnIndex(await sliceOf(page, arranged), 'F1');
    await expect(cellAt(page, colF1, 0)).toHaveText(shows(2469), { timeout: 30_000 });

    const picker = page.locator('input[type="file"][accept=".xlsx"]');
    await expect(picker).toBeAttached({ timeout: 30_000 });
    await picker.setInputFiles({
      name: 'book.xlsx',
      mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
      buffer: book,
    });

    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible({ timeout: 60_000 });
    await expect(dialog.getByText('This file cannot be applied as it is')).toHaveCount(0);
    const apply = dialog.getByRole('button', { name: 'Apply' });
    await expect(apply).toBeEnabled();
    await apply.click();
    await expect(dialog).toBeHidden({ timeout: 60_000 });

    // W-1: задача формул після застосування → сітка сама перечитує зріз (без F9 і перезавантаження).
    await expect(cellAt(page, colF1, 0)).toHaveText(shows(3001), { timeout: 60_000 });
    await expect
      .poll(async () => Number((await sliceOf(page, arranged)).rows[0]?.cells['F1']), { timeout: 30_000 })
      .toBe(3001);
  });
});
