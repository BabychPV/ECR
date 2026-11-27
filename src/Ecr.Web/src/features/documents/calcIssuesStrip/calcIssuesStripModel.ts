import type { DocumentTableDto } from '@/api/types';
import { buildIssueCauses, type IssueCause, type IssueCauseKind } from '../calculationIssuesPanelModel';
import type { MonthUnmatchedRows } from '../useUnmatchedRows';

/** Причина за ВСІ місяці смуги: сума і клітинки матриці «причина × місяць». */
export interface StripReason {
  readonly kind: IssueCauseKind;
  readonly total: number;
  /** Місяць → причини цього виду (у місяці їх може бути кілька: різні коди формул, різні джерела). */
  readonly byMonth: ReadonlyMap<number, readonly IssueCause[]>;
}

export interface StripSummary {
  /** Місяці з проблемами, за зростанням. */
  readonly months: readonly number[];
  /** Причини в порядку тяжкості (блокувальні першими), далі — за сумою. */
  readonly reasons: readonly StripReason[];
  /** Є помилка формули або не затверджене джерело — подання заблоковано (той самий критерій, що в панелі B78). */
  readonly blocking: boolean;
}

/** Тяжкість виду: менше — вище в смузі (блокувальні й «чому не пораховано» першими, кількісна інформація — наприкінці). */
const Severity: Readonly<Record<IssueCauseKind, number>> = {
  sourceNotApproved: 0,
  formula: 1,
  hse400: 2,
  blocked: 3,
  stepError: 4,
  sourceChanged: 5,
  sourceOther: 6,
  missingInputs: 7,
  rejected: 8,
  unmatched: 9,
  inactive: 10,
};

/**
 * ✎ b14 doc-notices-compact: проблеми розрахунку ВСІХ місяців — одна смуга. Причини — ті самі, що в панелі B78
 * (`buildIssueCauses`, той самий перехід «Show rows»), лише зведені за видом через місяці.
 */
export function summarizeCalcIssues(
  months: readonly MonthUnmatchedRows[],
  tables: readonly DocumentTableDto[],
): StripSummary {
  const byKind = new Map<IssueCauseKind, { total: number; byMonth: Map<number, IssueCause[]> }>();
  const withIssues: number[] = [];
  let blocking = false;

  for (const month of [...months].sort((a, b) => a.periodKey - b.periodKey)) {
    const causes = buildIssueCauses(month.rows, tables);
    if (causes.length === 0) continue;
    withIssues.push(month.periodKey);
    if (month.rows.formulaErrors.length > 0 || causes.some((cause) => cause.kind === 'sourceNotApproved')) blocking = true;

    for (const cause of causes) {
      const entry = byKind.get(cause.kind) ?? { total: 0, byMonth: new Map<number, IssueCause[]>() };
      entry.total += cause.count;
      entry.byMonth.set(month.periodKey, [...(entry.byMonth.get(month.periodKey) ?? []), cause]);
      byKind.set(cause.kind, entry);
    }
  }

  const reasons = [...byKind]
    .map(([kind, entry]) => ({ kind, total: entry.total, byMonth: entry.byMonth }))
    .sort((a, b) => Severity[a.kind] - Severity[b.kind] || b.total - a.total);

  return { months: withIssues, reasons, blocking };
}

/** Сума кількостей причин виду за місяць (клітинка матриці). */
export function monthCount(reason: StripReason, periodKey: number): number {
  return (reason.byMonth.get(periodKey) ?? []).reduce((sum, cause) => sum + cause.count, 0);
}

/**
 * ✎ DOCUX L1 (принцип 8 плану): «Details розгорнуто» пам'ятається лише в межах ВІДКРИТОГО документа (пам'ять вкладки),
 * а не на людину між документами. Доти стан ішов у localStorage і кожен наступний документ відкривався з розгорнутою
 * матрицею й технічними панелями — пів екрана. Ключ — документ (без нього — `anon`, як сторінка без `documentId`).
 */
const detailsOpenByDocument = new Map<string, boolean>();

function detailsKey(documentId: number | undefined): string {
  return documentId === undefined ? 'anon' : String(documentId);
}

/** Чи розгорнуто Details на цьому документі за цю сесію вкладки; за замовчуванням — згорнуто. */
export function readDetailsOpen(documentId: number | undefined): boolean {
  return detailsOpenByDocument.get(detailsKey(documentId)) === true;
}

/** Запам'ятати стан для цього документа (до перезавантаження вкладки). */
export function writeDetailsOpen(documentId: number | undefined, open: boolean): void {
  detailsOpenByDocument.set(detailsKey(documentId), open);
}

/** Для тестів: забути стан усіх документів. */
export function resetDetailsOpen(): void {
  detailsOpenByDocument.clear();
}
