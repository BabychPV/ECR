import { compare, type CompareReport, type ExpectedValue } from './compare.ts';
import type { EcrClient } from './client.ts';
import type { Mapping } from './config.ts';
import { parseNumber, type CsvRow } from './csv.ts';

export interface PreparedRow {
  rowKey: string;
  inputs: Array<{ columnCode: string; value: number }>;
  expected: ExpectedValue[];
}

/** CSV + Mapping → рядки для запису й еталонні викиди. Порожні вхідні клітинки пропускаються. */
export function prepareRows(csv: CsvRow[], mapping: Mapping): PreparedRow[] {
  return csv.map((row, i) => {
    const rowKey = (mapping.rowKeyColumn ? row[mapping.rowKeyColumn] : undefined) || `r${i + 1}`;
    const inputs: PreparedRow['inputs'] = [];
    for (const [csvCol, columnCode] of Object.entries(mapping.inputs)) {
      if (!(csvCol in row)) throw new Error(`CSV: немає колонки «${csvCol}» (рядок ${i + 2})`);
      const value = parseNumber(row[csvCol]!);
      if (value !== null) inputs.push({ columnCode, value });
    }
    const expected: ExpectedValue[] = [];
    for (const [csvCol, outputCode] of Object.entries(mapping.expected)) {
      if (!(csvCol in row)) throw new Error(`CSV: немає колонки «${csvCol}» (рядок ${i + 2})`);
      const value = parseNumber(row[csvCol]!);
      if (value === null) throw new Error(`CSV: «${csvCol}» у рядку ${i + 2} не число`);
      expected.push({ rowKey, outputCode, value });
    }
    return { rowKey, inputs, expected };
  });
}

export interface RunResult {
  documentId: number;
  report: CompareReport;
}

export async function runRegression(
  client: EcrClient,
  mapping: Mapping,
  rows: PreparedRow[],
  opts: { pollMs?: number; timeoutMs?: number; sleep?: (ms: number) => Promise<void> } = {},
): Promise<RunResult> {
  const sleep = opts.sleep ?? ((ms) => new Promise((r) => setTimeout(r, ms)));
  const pollMs = opts.pollMs ?? 1000;
  const timeoutMs = opts.timeoutMs ?? 300_000;

  const { documentId } = await client.createDocument({
    projectId: mapping.projectId,
    sheetDefIds: [mapping.sheetDefId],
    templateVersionId: mapping.templateVersionId,
  });
  const table = (await client.tables(documentId, mapping.periodKey))
    .find((t) => t.sheetDefId === mapping.sheetDefId && t.tableCode === mapping.tableCode);
  if (!table) throw new Error(`Таблицю «${mapping.tableCode}» не знайдено в документі ${documentId}`);

  for (const row of rows) await client.createRow(documentId, table.tableInstanceId, row.rowKey);
  await client.patchCells(documentId, {
    tableInstanceId: table.tableInstanceId,
    periodKey: mapping.periodKey,
    origin: 'UserEdit',
    rows: rows.map((r) => ({
      rowKey: r.rowKey,
      baseVersion: null,
      cells: r.inputs.map((c) => ({ columnCode: c.columnCode, value: c.value })),
    })),
  });

  const { jobId } = await client.recalculate(documentId, mapping.periodKey, mapping.sheetDefId);
  let waited = 0;
  for (;;) {
    const job = await client.job(jobId);
    if (job.state === 'Succeeded') break;
    if (job.state === 'Failed' || job.state === 'Cancelled') {
      throw new Error(`Перерахунок ${job.state}: ${job.error ?? job.message ?? ''}`);
    }
    if (waited >= timeoutMs) throw new Error(`Перерахунок не завершився за ${timeoutMs} мс (стан ${job.state})`);
    await sleep(pollMs);
    waited += pollMs;
  }

  const actual = await client.calculationResults(documentId, mapping.periodKey);
  const report = compare(rows.flatMap((r) => r.expected), actual, mapping.tolerance ?? 1e-6, {
    reportUnexpected: mapping.reportUnexpected,
  });
  return { documentId, report };
}
