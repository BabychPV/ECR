// Порівняння очікуваних викидів із результатами розрахунку.

export interface ExpectedValue {
  /** Ключ рядка джерела (RowKey), до якого належить викид. */
  rowKey: string;
  outputCode: string;
  value: number;
}

export interface ActualValue {
  sourceRowKey: string | null;
  outputCode: string;
  value: number;
}

export type MismatchKind = 'differs' | 'missing' | 'unexpected';

export interface Mismatch {
  kind: MismatchKind;
  rowKey: string;
  outputCode: string;
  expected: number | null;
  actual: number | null;
  relError: number | null;
}

export interface CompareReport {
  total: number;
  matched: number;
  mismatches: Mismatch[];
}

/**
 * Відносна похибка |a−e| / max(|e|, абсолютний_поріг). Коли еталон 0, ділення
 * не має сенсу — тоді міряється абсолютна різниця проти `absFloor`.
 */
export function relativeError(expected: number, actual: number, absFloor = 1e-12): number {
  return Math.abs(actual - expected) / Math.max(Math.abs(expected), absFloor);
}

export function withinTolerance(expected: number, actual: number, tol: number): boolean {
  if (expected === actual) return true;
  if (expected === 0) return Math.abs(actual) <= tol;
  return relativeError(expected, actual) <= tol;
}

const keyOf = (rowKey: string, outputCode: string) => `${rowKey}\u0000${outputCode}`;

export function compare(
  expected: ExpectedValue[],
  actual: ActualValue[],
  tolerance = 1e-6,
  opts: { reportUnexpected?: boolean } = {},
): CompareReport {
  const actualByKey = new Map<string, ActualValue>();
  for (const a of actual) actualByKey.set(keyOf(a.sourceRowKey ?? '', a.outputCode), a);

  const mismatches: Mismatch[] = [];
  let matched = 0;
  const seen = new Set<string>();
  for (const e of expected) {
    const k = keyOf(e.rowKey, e.outputCode);
    seen.add(k);
    const a = actualByKey.get(k);
    if (!a) {
      mismatches.push({ kind: 'missing', rowKey: e.rowKey, outputCode: e.outputCode, expected: e.value, actual: null, relError: null });
    } else if (!withinTolerance(e.value, a.value, tolerance)) {
      mismatches.push({
        kind: 'differs', rowKey: e.rowKey, outputCode: e.outputCode,
        expected: e.value, actual: a.value, relError: relativeError(e.value, a.value),
      });
    } else {
      matched++;
    }
  }
  if (opts.reportUnexpected) {
    for (const a of actual) {
      const k = keyOf(a.sourceRowKey ?? '', a.outputCode);
      if (!seen.has(k)) {
        mismatches.push({ kind: 'unexpected', rowKey: a.sourceRowKey ?? '', outputCode: a.outputCode, expected: null, actual: a.value, relError: null });
      }
    }
  }
  return { total: expected.length, matched, mismatches };
}

export function formatReport(report: CompareReport): string {
  const lines = [`Збіглося ${report.matched} з ${report.total}; розбіжностей: ${report.mismatches.length}`];
  if (report.mismatches.length === 0) return lines.join('\n');
  const rows = report.mismatches.map((m) => [
    m.kind, m.rowKey, m.outputCode, m.expected?.toString() ?? '—', m.actual?.toString() ?? '—',
    m.relError === null ? '—' : m.relError.toExponential(2),
  ]);
  const head = ['вид', 'рядок', 'викид', 'очікувано', 'отримано', 'відн.похибка'];
  const widths = head.map((h, i) => Math.max(h.length, ...rows.map((r) => r[i]!.length)));
  const fmt = (r: string[]) => r.map((c, i) => c.padEnd(widths[i]!)).join('  ');
  lines.push(fmt(head), ...rows.map(fmt));
  return lines.join('\n');
}
