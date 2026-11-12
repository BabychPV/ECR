import { inflateRawSync, crc32 } from 'node:zlib';
import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { signIn } from './multiSheet';

/**
 * ✎ B20 + B27 (F13 #829, C-3): імпорт Excel у документ з копією аркуша, проти РЕАЛЬНОГО сервера стенда
 * (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep excelImportSheetCopies`).
 *
 * Сценарій: шаблон з аркушем `isCopyable` («E2E Vessel», таблиця «Vessel data», вхідна колонка A) → документ → копія
 * аркуша («E2E Vessel 1» / «E2E Vessel 2») → по рядку в оригіналі й копії → експорт → ПІСЛЯ експорту той самий рядок
 * правиться в застосунку на обох аркушах (версія рядка змінюється — конфлікт «змінено після експорту») → у книзі той
 * самий рядок змінено на обох аркушах → імпорт через діалог:
 * - B20: рядки перегляду називають свій аркуш-копію («E2E Vessel 1 · Vessel data», «E2E Vessel 2 · Vessel data»);
 * - B27: «перезаписати» позначено лише для копії → копія отримує значення з файлу, оригінал лишає значення застосунку.
 *
 * ⛔ До B20: обидва рядки «Vessel data · R…» (один і той самий підпис). До B27: один прапорець на обидві копії, і
 * перезапис копії переписував і оригінал (1500.5 замість 1111.25).
 *
 * ⚠ Дані одноразові (як `sheetCopies.spec.ts`): шаблон/проєкт після публікації API не видаляє — прибирання немає,
 * імена унікальні за міткою часу. Без стенда файл ПАДАЄ (вхід/запити до API), а не пропускається (honesty-guard).
 */
test.describe.configure({ mode: 'serial', timeout: 300_000 });

const SheetName = 'E2E Vessel';
const TableName = 'Vessel data';

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

interface Slice {
  rows: { rowKey: string; rowVersion: string; cells: Record<string, unknown> }[];
}

interface Arranged {
  documentId: number;
  periodKey: number;
  originalInstance: number;
  copyInstance: number;
}

const sliceOf = async (page: Page, documentId: number, tableInstanceId: number): Promise<Slice> =>
  ok<Slice>(await page.request.get(`/api/v1/documents/${String(documentId)}/tables/${String(tableInstanceId)}`), 'зріз');

/** Записує A першого рядка екземпляра (як правка в сітці). */
async function writeA(page: Page, a: Arranged, tableInstanceId: number, value: string): Promise<void> {
  const row = (await sliceOf(page, a.documentId, tableInstanceId)).rows[0];
  expect(row, `у екземпляра ${String(tableInstanceId)} немає рядка`).toBeDefined();
  await ok(
    await page.request.patch(`/api/v1/documents/${String(a.documentId)}/cells`, {
      data: {
        tableInstanceId,
        periodKey: a.periodKey,
        origin: 'UserEdit',
        rows: [{ rowKey: row?.rowKey, baseVersion: row?.rowVersion, cells: [{ columnCode: 'A', value, isEmpty: false }] }],
      },
    }),
    `A = ${value} в екземплярі ${String(tableInstanceId)}`,
  );
}

const valueOfA = async (page: Page, a: Arranged, tableInstanceId: number): Promise<number> =>
  Number((await sliceOf(page, a.documentId, tableInstanceId)).rows[0]?.cells['A']);

async function arrange(page: Page): Promise<Arranged> {
  const stamp = String(Date.now());
  const template = await ok<{ templateId: number }>(
    await page.request.post('/api/v1/templates', { data: { code: `E2EXc${stamp}`, nameL10n: { en: `E2E xlsx copies ${stamp}` } } }),
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
    await page.request.put(`${base}/sheets/VESSEL`, {
      data: { nameL10n: { en: SheetName }, ordinal: 1, isMandatory: true, isVisible: true, sheetGroup: null, isCopyable: true },
    }),
    'копійований аркуш',
  );
  const table = await ok<{ id: number }>(
    await page.request.put(`${base}/sheets/VESSEL/tables/T1`, {
      data: { nameL10n: { en: TableName }, ordinal: 0, layoutKind: 'Static', rowMode: 'Dynamic', maxDynamicRows: 50 },
    }),
    'таблиця',
  );
  await ok(
    await page.request.put(`${base}/tables/${String(table.id)}/columns/A`, {
      data: {
        headerL10n: { en: 'A' },
        ordinal: 0,
        dataType: 'Decimal',
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
      },
    }),
    'колонка A',
  );
  await ok(await page.request.post(`${base}/publish`, { data: { reason: 'e2e: Excel import on sheet copies' } }), 'публікація');

  const policies = await ok<{ id: number; code: string }[]>(await page.request.get('/api/v1/projects/period-policies'), 'політики');
  const policy = policies.find((item) => item.code === 'ECR-Standard') ?? policies[0];
  expect(policy, 'у стенда немає жодної політики періодів').toBeDefined();
  const project = await ok<{ projectId: number }>(
    await page.request.post('/api/v1/projects', {
      data: {
        code: `E2EXcPrj${stamp}`,
        nameL10n: { en: `E2E xlsx copies ${stamp}` },
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
      data: { projectId: project.projectId, sheetDefIds: structure.sheets.map((sheet) => sheet.id), templateVersionId: version.versionId },
    }),
    'документ',
  );
  const calendar = await ok<{ periods: { periodKey: number; state: string }[] }>(
    await page.request.get(`/api/v1/projects/${String(project.projectId)}/periods`),
    'календар',
  );
  const periodKey = (calendar.periods.find((item) => item.state === 'Open') ?? calendar.periods[0])?.periodKey ?? 0;
  const documentUrl = `/api/v1/documents/${String(document.documentId)}`;

  const before = await ok<{ sheetDefId: number }[]>(await page.request.get(`${documentUrl}/tables?periodKey=${String(periodKey)}`), 'таблиці');
  await ok(await page.request.post(`${documentUrl}/sheets/${String(before[0]?.sheetDefId)}/copies`), 'копія аркуша');

  const tables = await ok<{ tableInstanceId: number; sheetCopyId?: number | null }[]>(
    await page.request.get(`${documentUrl}/tables?periodKey=${String(periodKey)}`),
    'таблиці після копії',
  );
  const original = tables.find((t) => t.sheetCopyId === undefined || t.sheetCopyId === null);
  const copy = tables.find((t) => t.sheetCopyId !== undefined && t.sheetCopyId !== null);
  expect(original, 'немає таблиці оригіналу').toBeDefined();
  expect(copy, 'немає таблиці копії').toBeDefined();

  const arranged: Arranged = {
    documentId: document.documentId,
    periodKey,
    originalInstance: original?.tableInstanceId ?? 0,
    copyInstance: copy?.tableInstanceId ?? 0,
  };

  for (const instance of [arranged.originalInstance, arranged.copyInstance]) {
    await ok(
      await page.request.post(`${documentUrl}/rows`, { data: { tableInstanceId: instance, rowKey: null } }),
      `рядок в екземплярі ${String(instance)}`,
    );
  }

  // Значення, унікальні в усій книзі (заміна в книзі шукає рівно одну комірку).
  await writeA(page, arranged, arranged.originalInstance, '1234.5');
  await writeA(page, arranged, arranged.copyInstance, '2345.5');

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

/** Частини .xlsx (zip): ім'я → вміст. Лише «stored» і «deflate» — ClosedXML інших не пише (як `excelImportRoundTrip`). */
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

/** Міняє числа в комірках аркушів книги: кожне `from` має стояти рівно в одній комірці. */
function replaceNumbers(book: Buffer, replacements: [string, string][]): Buffer {
  const parts = unzip(book);
  const pattern = /<((?:\w+:)?v)>([^<]*)<\/\1>/g;
  const counts = new Map<string, number>(replacements.map(([from]) => [from, 0]));
  for (const [name, data] of parts) {
    if (!name.startsWith('xl/worksheets/')) continue;
    const xml = data.toString('utf8');
    const next = xml.replace(pattern, (match, tag: string, value: string) => {
      const hit = value.trim() === '' ? undefined : replacements.find(([from]) => Number(value) === Number(from));
      if (hit === undefined) return match;
      counts.set(hit[0], (counts.get(hit[0]) ?? 0) + 1);
      return `<${tag}>${hit[1]}</${tag}>`;
    });
    if (next !== xml) parts.set(name, Buffer.from(next, 'utf8'));
  }
  for (const [from, count] of counts) expect(count, `число ${from} у книзі`).toBe(1);
  return zip(parts);
}

test.describe('Excel import on sheet copies', () => {
  test('excelImportSheetCopies: preview names the sheet copy; overwrite ticked for the copy changes only the copy', async ({ page }) => {
    await signIn(page);
    const arranged = await arrange(page);

    const exported = await exportWorkbook(page, arranged);

    // ПІСЛЯ експорту той самий рядок правиться в застосунку на обох аркушах — версія рядка змінюється.
    await writeA(page, arranged, arranged.originalInstance, '1111.25');
    await writeA(page, arranged, arranged.copyInstance, '2222.25');

    // …а в книзі той самий рядок змінено на обох аркушах: обидва — конфлікти «змінено після експорту».
    const book = replaceNumbers(exported, [
      ['1234.5', '1500.5'],
      ['2345.5', '2600.5'],
    ]);

    await page.goto(`/documents/${String(arranged.documentId)}?periodKey=${String(arranged.periodKey)}`);
    await expect(page.locator('revo-grid:visible').first()).toBeVisible({ timeout: 60_000 });

    const picker = page.locator('input[type="file"][accept=".xlsx"]');
    await expect(picker).toBeAttached({ timeout: 30_000 });
    await picker.setInputFiles({
      name: 'book.xlsx',
      mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
      buffer: book,
    });

    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible({ timeout: 60_000 });

    // B20: кожен конфліктний рядок названо своїм аркушем-копією.
    const copyRow = dialog.getByRole('checkbox', { name: new RegExp(`${SheetName} 2 · ${TableName}`) });
    const originalRow = dialog.getByRole('checkbox', { name: new RegExp(`${SheetName} 1 · ${TableName}`) });
    await expect(copyRow).toBeVisible({ timeout: 30_000 });
    await expect(originalRow).toBeVisible();
    await expect(dialog.getByRole('table', { name: new RegExp(`${SheetName} 2 · ${TableName}`) })).toContainText('2600.5');
    await expect(dialog.getByRole('table', { name: new RegExp(`${SheetName} 1 · ${TableName}`) })).toContainText('1500.5');

    // B27: перезаписати лише копію; оригінал лишає чуже — явною згодою.
    await copyRow.check();
    await dialog.getByRole('checkbox', { name: /Apply the rest of the file/ }).check();

    const apply = dialog.getByRole('button', { name: 'Apply' });
    await expect(apply).toBeEnabled();
    await apply.click();
    await expect(dialog).toBeHidden({ timeout: 60_000 });

    // Копія — значення з файлу; оригінал — значення застосунку (до B27 — теж 1500.5).
    await expect.poll(async () => valueOfA(page, arranged, arranged.copyInstance), { timeout: 120_000 }).toBe(2600.5);
    expect(await valueOfA(page, arranged, arranged.originalInstance)).toBe(1111.25);
  });
});
