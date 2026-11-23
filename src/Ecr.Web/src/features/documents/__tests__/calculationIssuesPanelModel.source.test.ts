import { describe, expect, it } from 'vitest';
import type { FormulaErrorIssue, SourceIssue } from '@/features/calculationIssues/api';
import { buildIssueCauses, sourceKindOf } from '@/features/documents/calculationIssuesPanelModel';
import type { UnmatchedRows } from '@/features/documents/useUnmatchedRows';
import { formatPeriodKey } from '@/shared/format/periodKey';

/**
 * ✎ B80 N4 (N6 частина B): причини джерела HSE400 (`CalculationIssuesDto.sourceIssues`) у моделі панелі.
 *
 * Мутації: поміняти місцями `SourceMonthNotApproved`/`SourceChangedAfterApproval` у `sourceKindOf` — червоне
 * «два види»; поставити джерело після `hse400` в `order` — червоне «першими».
 */

function source(over: Partial<SourceIssue>): SourceIssue {
  return {
    kind: 'SourceMonthNotApproved',
    monthNumber: 3,
    sourcePeriodKey: 202603,
    sourceDocumentId: 412,
    sourceProjectCode: 'KPO-2026',
    sourceDocumentKey: 'HSE400-2026',
    sourceApprovedAt: null,
    approvedAt: null,
    ...over,
  };
}

const Hse400Formula = "REGFIELD(REGFIND('LAND_HSE400_COMPOSITION', [Period].Year), 'VALUE')";

function hse400Error(): FormulaErrorIssue {
  return {
    tableCode: 'T1',
    sheetCopyId: null,
    rowKey: 'R1',
    rowNumber: 1,
    columnCode: 'Density',
    columnHeaderL10n: { values: { en: 'Density' } },
    kind: 'Error',
    errorCode: '#N/A',
    formula: Hse400Formula,
    inputs: [],
    source: null,
    since: '2026-11-13T10:00:00Z',
    rowLabelL10n: null,
    dependentCount: 0,
  };
}

function rows(sourceIssues: readonly SourceIssue[] | undefined, formulaErrors: FormulaErrorIssue[] = []): UnmatchedRows {
  return { issues: [], hints: [], finishedAt: null, formulaErrors, ...(sourceIssues === undefined ? {} : { sourceIssues }) };
}

describe('calculationIssuesPanelModel: змінені дані джерела за Any (F5-R, D-570)', () => {
  /** Мутація: прибрати `variant` з ключа групи — червоне (одна причина замість двох); `latestChange` → перший — червоне. */
  it('окрема причина від ApprovedOnly, дата зміни — найпізніша в групі', () => {
    const causes = buildIssueCauses(
      rows([
        source({ kind: 'SourceChangedAfterApproval', sourceApprovedAt: '2026-04-10T09:00:00Z' }),
        source({ kind: 'SourceChangedAfterApproval', monthNumber: 4, sourcePeriodKey: 202604, sourceChangedAt: '2026-04-20T10:00:00Z' }),
        source({ kind: 'SourceChangedAfterApproval', monthNumber: 5, sourcePeriodKey: 202605, sourceChangedAt: '2026-05-02T10:00:00Z' }),
      ]),
      [],
    );

    expect(causes).toHaveLength(2);
    const data = causes.find((cause) => (cause.source?.sourceChangedAt ?? null) !== null)!;
    expect(data.kind).toBe('sourceChanged');
    expect(data.count).toBe(2);
    expect(data.source?.sourceChangedAt).toBe('2026-05-02T10:00:00Z');
    expect(causes.find((cause) => cause !== data)?.source?.sourceChangedAt).toBeNull();
  });
});

describe('calculationIssuesPanelModel: причини джерела (B80 N4)', () => {
  it('два види і невідомий вид — окремі причини; невідомий не ламає модель', () => {
    expect(sourceKindOf('SourceMonthNotApproved')).toBe('sourceNotApproved');
    expect(sourceKindOf('SourceChangedAfterApproval')).toBe('sourceChanged');
    expect(sourceKindOf('SomethingNew')).toBe('sourceOther');

    const causes = buildIssueCauses(
      rows([
        source({}),
        source({ kind: 'SourceChangedAfterApproval', monthNumber: 1, sourcePeriodKey: 202601 }),
        source({ kind: 'SomethingNew', monthNumber: 2, sourcePeriodKey: 202602 }),
      ]),
      [],
    );

    expect(causes.map((cause) => cause.kind)).toEqual(['sourceNotApproved', 'sourceChanged', 'sourceOther']);
    expect(causes.every((cause) => cause.unit === 'months')).toBe(true);
  });

  it('лічильник — різні місяці споживача; назви — місяці джерела за зростанням, без повторів', () => {
    const [cause] = buildIssueCauses(
      rows([
        source({ monthNumber: 5, sourcePeriodKey: 202605 }),
        source({ monthNumber: 2, sourcePeriodKey: 202602 }),
        // Той самий місяць споживача вдруге (інший аркуш джерела) — не рахується двічі.
        source({ monthNumber: 5, sourcePeriodKey: 202605 }),
      ]),
      [],
    );

    expect(cause!.count).toBe(2);
    expect(cause!.subjects).toEqual([formatPeriodKey(202602), formatPeriodKey(202605)]);
    // Перехід — на найраніший місяць джерела.
    expect(cause!.source).toEqual({
      documentId: 412,
      periodKey: 202602,
      label: 'KPO-2026 · HSE400-2026',
      sourceApprovedAt: null,
      approvedAt: null,
      // ✎ F5-R: рядок ApprovedOnly — моменту зміни даних джерела немає.
      sourceChangedAt: null,
    });
  });

  it('причини джерела — ПЕРШИМИ, перед причиною HSE400, яку вони пояснюють', () => {
    const causes = buildIssueCauses(rows([source({ kind: 'SourceChangedAfterApproval' })], [hse400Error()]), []);

    expect(causes.map((cause) => cause.kind)).toEqual(['sourceChanged', 'hse400']);
  });

  it('приховане джерело (усі поля джерела null) — без посилання, назви й дати джерела', () => {
    const [cause] = buildIssueCauses(
      rows([
        source({
          kind: 'SourceChangedAfterApproval',
          sourceDocumentId: null,
          sourceProjectCode: null,
          sourceDocumentKey: null,
          sourceApprovedAt: null,
          approvedAt: '2026-04-02T08:00:00Z',
        }),
      ]),
      [],
    );

    expect(cause!.source).toEqual({
      documentId: null,
      periodKey: 202603,
      label: null,
      sourceApprovedAt: null,
      approvedAt: '2026-04-02T08:00:00Z',
      sourceChangedAt: null,
    });
  });

  it('видиме і приховане джерело одного виду — дві причини, посилання лише у видимої', () => {
    const causes = buildIssueCauses(
      rows([source({}), source({ monthNumber: 4, sourcePeriodKey: 202604, sourceDocumentId: null, sourceDocumentKey: null, sourceProjectCode: null })]),
      [],
    );

    expect(causes.map((cause) => cause.source?.documentId ?? null)).toEqual([412, null]);
  });

  it('sourceIssues немає (null на воротах «any» чи старий сервер) — причин джерела немає', () => {
    expect(buildIssueCauses(rows(undefined), [])).toEqual([]);
    expect(buildIssueCauses(rows([]), [])).toEqual([]);
  });
});
