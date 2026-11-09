import { describe, expect, it } from 'vitest';
import type { DocumentTableDto } from '@/api/types';
import { groupBySheet, recalculatedAfterSubmissionOf, sheetItemsOf, stateOfGroup } from '../sheetGroups';

type CopyFields = Partial<Pick<DocumentTableDto, 'sheetCopyId' | 'copyOrdinal' | 'isCopyable' | 'displayNameL10n'>>;

/** Таблиця документа з полями копій за контрактом (`DocumentTableDto`). */
function table(tableInstanceId: number, sheetCode: string, copy: CopyFields = {}, sheetOrdinal = 1): DocumentTableDto {
  return {
    allowsDynamicRows: false,
    copyOrdinal: 1,
    isCopyable: false,
    maxDynamicRows: null,
    sheetCode,
    sheetDefId: sheetOrdinal * 10,
    sheetNameL10n: { values: { en: `Sheet ${sheetCode}` } },
    sheetOrdinal,
    tableCode: `T${String(tableInstanceId)}`,
    tableDefId: tableInstanceId,
    tableInstanceId,
    tableNameL10n: { values: { en: 'Table' } },
    tableOrdinal: 1,
    ...copy,
  } as DocumentTableDto;
}

describe('groupBySheet: вкладки копій аркуша', () => {
  it('аркуш без копій — одна вкладка з назвою без номера', () => {
    const [only] = groupBySheet([table(1, 'GEN', { isCopyable: true, copyOrdinal: 1 })]);

    expect(only?.name).toBe('Sheet GEN');
    expect(only?.key).toBe('GEN');
  });

  it('є копії — копія «<Назва> N» за copyOrdinal, оригінал «<Назва> 1»; ключі різні', () => {
    const groups = groupBySheet([
      table(1, 'GEN', { sheetCopyId: null, copyOrdinal: 1, isCopyable: true }),
      table(2, 'GEN', { sheetCopyId: 77, copyOrdinal: 2, isCopyable: true }),
      table(3, 'GEN', { sheetCopyId: 78, copyOrdinal: 3, isCopyable: true }),
    ]);

    expect(groups.map((g) => g.name)).toEqual(['Sheet GEN 1', 'Sheet GEN 2', 'Sheet GEN 3']);
    expect(groups.map((g) => g.key)).toEqual(['GEN', 'GEN~77', 'GEN~78']);
    // Код аркуша спільний; ключі стану (`sheetStates`) — `КОД` для оригіналу і `КОД#id` для копій.
    expect(groups.every((g) => g.code === 'GEN')).toBe(true);
    expect(groups.map((g) => g.stateKey)).toEqual(['GEN', 'GEN#77', 'GEN#78']);
  });

  it('sheetCopyId відсутній у відповіді (необов\'язкове поле) — це оригінал', () => {
    const [only] = groupBySheet([table(1, 'GEN')]);

    expect(only?.sheetCopyId).toBeNull();
    expect(only?.stateKey).toBe('GEN');
  });

  it('перейменована група: displayNameL10n замінює назву шаблону на всіх вкладках', () => {
    const renamed = { values: { en: 'Boiler' } };
    const groups = groupBySheet([
      table(1, 'GEN', { sheetCopyId: null, copyOrdinal: 1, displayNameL10n: renamed }),
      table(2, 'GEN', { sheetCopyId: 77, copyOrdinal: 2, displayNameL10n: renamed }),
    ]);

    expect(groups.map((g) => g.name)).toEqual(['Boiler 1', 'Boiler 2']);
    expect(groups[1]?.groupNameValues).toEqual({ en: 'Boiler' });
  });

  /*
   * ✎ debt-1109 SHEETNAME-P3 (F13 p218): сервер віддає показувану назву вже з номером («Raft 1», «Raft 2» —
   * `SheetCopyNaming.Display`), а вкладка й відмова «Sheet Raft 1 1 is submitted…» повторювали номер.
   * ⛔ Червоне без фіксу: ['Raft 1 1', 'Raft 2 2'].
   */
  it('назва сервера з номером («Raft 1») — номер не повторюється; без копій назву людини не чіпаємо', () => {
    const groups = groupBySheet([
      table(1, 'GEN', { sheetCopyId: null, copyOrdinal: 1, displayNameL10n: { values: { en: 'Raft 1' } } }),
      table(2, 'GEN', { sheetCopyId: 77, copyOrdinal: 2, displayNameL10n: { values: { en: 'Raft 2' } } }),
      table(3, 'AIR', { sheetCopyId: null, copyOrdinal: 1, displayNameL10n: { values: { en: 'Unit 1' } } }, 2),
    ]);

    expect(groups.map((g) => g.name)).toEqual(['Raft 1', 'Raft 2', 'Unit 1']);
    expect(groups[1]?.groupNameValues).toEqual({ en: 'Raft' });
    expect(groups[2]?.groupNameValues).toEqual({ en: 'Unit 1' });
  });

  it('кілька таблиць однієї вкладки лишаються в ній; копії стоять після оригіналу, перед наступним аркушем', () => {
    const groups = groupBySheet([
      table(5, 'AIR', {}, 2),
      table(2, 'GEN', { sheetCopyId: 77, copyOrdinal: 2 }, 1),
      table(1, 'GEN', { sheetCopyId: null, copyOrdinal: 1 }, 1),
      table(3, 'GEN', { sheetCopyId: null, copyOrdinal: 1 }, 1),
    ]);

    expect(groups.map((g) => g.key)).toEqual(['GEN', 'GEN~77', 'AIR']);
    expect(groups[0]?.tables.map((x) => x.tableInstanceId)).toEqual([1, 3]);
  });

  it('активна вкладка-копія (DocumentPage) читає власний стан «КОД#id», а не стан оригіналу', () => {
    const [original, copy] = groupBySheet([
      table(1, 'GEN', { sheetCopyId: null, copyOrdinal: 1 }),
      table(2, 'GEN', { sheetCopyId: 77, copyOrdinal: 2 }),
    ]);
    const states = { GEN: 'Submitted', 'GEN#77': 'Rejected' };

    expect(original && stateOfGroup(original, states)).toBe('Submitted');
    expect(copy && stateOfGroup(copy, states)).toBe('Rejected');
  });

  it('SHEETNAME: copy marker — подана копія бачить свою позначку «перераховано після подання», а не оригіналу', () => {
    // F13 p218 #788: DocumentPage шукав позначку за голим кодом («VESSELS»), а ключ копії — «VESSELS#1829».
    // Мутація: порівнювати з кодом аркуша (`stateKey` до «#») — червоніють обидва твердження.
    const [original, copy] = groupBySheet([
      table(1, 'VESSELS', { sheetCopyId: null, copyOrdinal: 1 }),
      table(2, 'VESSELS', { sheetCopyId: 1829, copyOrdinal: 2 }),
    ]);
    const copyMarked = [
      { code: 'VESSELS', recalculatedAfterSubmission: false },
      { code: 'VESSELS#1829', recalculatedAfterSubmission: true },
    ];
    const originalMarked = [
      { code: 'VESSELS', recalculatedAfterSubmission: true },
      { code: 'VESSELS#1829', recalculatedAfterSubmission: false },
    ];

    expect(copy && recalculatedAfterSubmissionOf(copy, copyMarked)).toBe(true);
    expect(original && recalculatedAfterSubmissionOf(original, copyMarked)).toBe(false);
    // Позначка оригіналу не протікає в копію.
    expect(copy && recalculatedAfterSubmissionOf(copy, originalMarked)).toBe(false);
    expect(original && recalculatedAfterSubmissionOf(original, originalMarked)).toBe(true);
  });

  it('смуга аркушів: код пункту — ключ вкладки, стан копії — з власного ключа «КОД#id», не з оригіналу', () => {
    const groups = groupBySheet([
      table(1, 'GEN', { sheetCopyId: null, copyOrdinal: 1 }),
      table(2, 'GEN', { sheetCopyId: 77, copyOrdinal: 2 }),
      table(3, 'GEN', { sheetCopyId: 78, copyOrdinal: 3 }),
    ]);

    expect(sheetItemsOf(groups, { GEN: 'Submitted', 'GEN#77': 'Approved' })).toEqual([
      { code: 'GEN', name: 'Sheet GEN 1', state: 'Submitted' },
      { code: 'GEN~77', name: 'Sheet GEN 2', state: 'Approved' },
      // Копії без запису в `sheetStates` — Draft, а не стан оригіналу.
      { code: 'GEN~78', name: 'Sheet GEN 3', state: 'Draft' },
    ]);
  });
});
