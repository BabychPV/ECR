import { describe, expect, it } from 'vitest';
import { boardColumns, parseDocumentsView } from '../documentsBoardModel';
import { documentYearState } from '../documentSheets';

/** `UI-40`: стовпці дошки за станом документа. */
const d = (id: number, sheetStates: Record<string, string>) => ({ id, projectId: 1, sheetStates });

/** ✎ b14: рядок переліку за РІК — станів аркушів немає (`sheetStates` порожній), є стан кожного періоду року. */
const y = (id: number, states: readonly string[], projectId = 1) => ({
  id,
  projectId,
  sheetStates: {},
  periodStates: states.map((state, index) => ({ periodKey: 202601 + index, state })),
});

const ids = (columns: readonly { readonly id: string; readonly items: readonly { readonly id: number }[] }[]) =>
  columns.map((column) => [column.id, column.items.map((item) => item.id)]);

describe('boardColumns', () => {
  it('розкладає за станом у порядку смуги лічильників і зберігає порядок переліку', () => {
    const columns = boardColumns([
      d(1, { A: 'Draft' }),
      d(2, { A: 'Approved', B: 'Submitted' }),
      d(3, { A: 'Rejected', B: 'Approved' }),
      d(4, { A: 'Approved' }),
      d(5, { A: 'Draft', B: 'Approved' }),
    ]);

    // ✎ b14: «частково подано» — власний стовпець (як власне число на смузі), а не в чернетках.
    expect(ids(columns)).toEqual([
      ['Draft', [1]],
      ['PartlySubmitted', [5]],
      ['Submitted', [2]],
      ['Rework', [3]],
      ['Approved', [4]],
    ]);
  });

  /*
   * ⛔ AC-1 (b14 documents-board-yearly): перелік завжди за рік — `sheetStates` порожній. Доти дошка брала саме його,
   * і всі 5 документів падали в «No state for this period», хоч лічильники над нею казали «4 Draft · 1 Partly submitted».
   * Без фіксу цей тест червоний: стовпці Draft і PartlySubmitted порожні, усе — у `NoState`.
   */
  it('AC-1: рядки за рік — у стовпцях за станом року; стовпця «без стану» немає', () => {
    const columns = boardColumns([
      y(1, ['Draft', 'Draft', 'Draft']),
      y(2, ['Draft', 'Draft']),
      y(3, ['Draft']),
      y(4, ['Draft', 'Draft', 'Draft', 'Draft']),
      // Партія з вересня перевідкрита: січ–сер подано, вересень — чернетка.
      y(5, ['Submitted', 'Approved', 'Draft']),
    ]);

    expect(ids(columns)).toEqual([
      ['Draft', [1, 2, 3, 4]],
      ['PartlySubmitted', [5]],
      ['Submitted', []],
      ['Rework', []],
      ['Approved', []],
    ]);
    expect(columns.map((column) => column.id)).not.toContain('NoState');
  });

  it('AC-1: стан за рік рахується лише по РОЗПОЧАТИХ періодах (Scheduled сервер віддає як Draft)', () => {
    // Січень і лютий подано, березень ще не відкрито (Scheduled → `Draft` у periodStates).
    const document = y(1, ['Submitted', 'Submitted', 'Draft']);
    const started = new Set([202601, 202602]);

    expect(boardColumns([document], () => started).find((column) => column.id === 'Submitted')?.items).toHaveLength(1);
    // Без календаря — усі періоди: «частково подано» (так і каже коментар `documentYearState`).
    expect(documentYearState(document)).toBe('PartlySubmitted');
  });

  it('стан, якого правило не знає (Returned), — у «Returned or rejected»', () => {
    const columns = boardColumns([d(1, { A: 'Returned' })]);

    expect(columns.find((column) => column.id === 'Rework')?.items.map((item) => item.id)).toEqual([1]);
  });

  it('документи без стану не губляться: окремий стовпець лише тоді', () => {
    expect(boardColumns([d(1, { A: 'Draft' })]).map((column) => column.id)).not.toContain('NoState');

    const columns = boardColumns([d(1, {}), d(2, { A: 'Mystery' })]);
    expect(columns.at(-1)).toMatchObject({ id: 'NoState' });
    expect(columns.at(-1)?.items.map((item) => item.id)).toEqual([1, 2]);
    // Сума по стовпцях — рівно кількість документів сторінки: жоден не зник і не задвоївся.
    expect(columns.reduce((sum, column) => sum + column.items.length, 0)).toBe(2);
  });

  it('B147: документ без погодження (workflowMode=None) — у власному стовпці, не в чернетках і не «без стану»', () => {
    // ⚠ Аркуші такого документа сервер віддає `Draft`; документ без аркушів — теж «без погодження».
    const columns = boardColumns([
      { id: 1, projectId: 1, sheetStates: { A: 'Draft' }, workflowMode: 'None' },
      { id: 2, projectId: 1, sheetStates: {}, workflowMode: 'None' },
      { id: 3, projectId: 1, sheetStates: { A: 'Draft' }, workflowMode: 'SubmitApprove' },
      // ✎ b14: за рік — усі розпочаті періоди без погодження.
      y(4, ['NoApproval', 'NoApproval']),
    ]);

    expect(ids(columns)).toEqual([
      ['Draft', [3]],
      ['PartlySubmitted', []],
      ['Submitted', []],
      ['Rework', []],
      ['Approved', []],
      ['NoApproval', [1, 2, 4]],
    ]);
    // Без таких документів стовпця немає.
    expect(boardColumns([d(1, { A: 'Draft' })]).map((column) => column.id)).not.toContain('NoApproval');
  });

  it('лише «Returned or rejected» чекає уваги', () => {
    expect(boardColumns([]).filter((column) => column.attention).map((column) => column.id)).toEqual(['Rework']);
  });
});

/**
 * ⛔ AC-1: правило року — дзеркало `DocumentListSummaryStore.SummarizeYearAsync` (лічильники) і `WhereYearState`
 * (фільтр): клітинки «аркуш × розпочатий період» без періодів «без погодження».
 */
describe('documentYearState', () => {
  const state = (...states: string[]) => documentYearState({ sheetStates: {}, periodStates: states.map((s, i) => ({ periodKey: 202601 + i, state: s })) });

  it.each([
    [['Draft', 'Draft'], 'Draft'],
    [['Submitted', 'Draft'], 'PartlySubmitted'],
    [['PartlySubmitted'], 'PartlySubmitted'],
    [['Approved', 'Draft'], 'PartlySubmitted'],
    [['Submitted', 'Approved'], 'Submitted'],
    [['Approved', 'Approved'], 'Approved'],
    [['Approved', 'Rejected', 'Draft'], 'Rejected'],
    // Період без погодження у «змішаного» документа просто не рахується.
    [['NoApproval', 'Approved'], 'Approved'],
    [['NoApproval', 'NoApproval'], 'NoApproval'],
    // Рік без періодів — сервер дає Draft (клітинок немає, `IsNone = 0`).
    [[], 'Draft'],
  ])('%j → %s', (states, expected) => {
    expect(state(...states)).toBe(expected);
  });

  it('рядок не за рік (periodStates = null) — колишній стан за місяць', () => {
    expect(documentYearState({ sheetStates: { A: 'Submitted' }, periodStates: null })).toBe('Submitted');
  });
});

describe('parseDocumentsView', () => {
  it('board — лише точне значення; решта — таблиця', () => {
    expect(parseDocumentsView('board')).toBe('board');
    expect(parseDocumentsView(null)).toBe('table');
    expect(parseDocumentsView('all')).toBe('table');
  });
});
