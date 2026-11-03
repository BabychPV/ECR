import { describe, expect, it } from 'vitest';
import type { DocumentTableDto } from '@/api/types';
import { groupBySheet, sheetItemsOf, stateOfGroup } from '../sheetGroups';

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
