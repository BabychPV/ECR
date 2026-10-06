import { describe, expect, it } from 'vitest';
import type { DocumentTableDto, ValidationFindingDto } from '@/api/types';
import { addressChip, countIssues, countOrDash, groupIssues } from '../inspectorModel';

function table(sheetCode: string, sheetOrdinal: number, tableDefId: number, tableOrdinal: number): DocumentTableDto {
  return {
    allowsDynamicRows: false,
    maxDynamicRows: null,
    sheetCode,
    sheetDefId: sheetOrdinal + 1,
    sheetNameL10n: { values: { en: `Sheet ${sheetCode}` } },
    sheetOrdinal,
    tableCode: `T${String(tableDefId)}`,
    tableDefId,
    tableInstanceId: tableDefId * 10,
    tableNameL10n: { values: { en: `Table ${String(tableDefId)}` } },
    tableOrdinal,
  } as DocumentTableDto;
}

function finding(tableDefId: number, severity: string, message: string): ValidationFindingDto {
  return { severity, ruleCode: 'CAP', message, tableDefId, rowKey: 'R1', columnCode: 'C1', blocksSave: severity === 'Error' };
}

describe('інспектор: групування зауважень (UI-25)', () => {
  it('групує за таблицями в порядку аркуша й таблиці, помилки першими', () => {
    const tables = [table('B', 1, 3, 0), table('A', 0, 2, 1), table('A', 0, 1, 0)];
    const groups = groupIssues(
      [finding(2, 'Warning', 'w2'), finding(3, 'Error', 'e3'), finding(2, 'Error', 'e2'), finding(1, 'Warning', 'w1')],
      tables,
    );

    expect(groups.map((group) => group.tableDefId)).toEqual([1, 2, 3]);
    expect(groups[1]?.issues.map((issue) => issue.finding.message)).toEqual(['e2', 'w2']);
    expect(groups[0]?.title).toBe('T1 Table 1');
  });

  it('⛔ зауваження таблиці, якої немає серед видимих, не показується й не рахується', () => {
    // Людина бачить лише аркуш A; сервер (до фіксу) віддав зауваження й аркуша B.
    const groups = groupIssues([finding(1, 'Error', 'visible'), finding(99, 'Error', 'hidden-sheet')], [table('A', 0, 1, 0)]);

    expect(groups).toHaveLength(1);
    expect(JSON.stringify(groups)).not.toContain('hidden-sheet');
    expect(countIssues(groups)).toEqual({ all: 1, errors: 1, warnings: 0 });
  });

  it('лічильники — помилки й попередження окремо', () => {
    const groups = groupIssues(
      [finding(1, 'Error', 'a'), finding(1, 'Warning', 'b'), finding(1, 'Warning', 'c')],
      [table('A', 0, 1, 0)],
    );

    expect(countIssues(groups)).toEqual({ all: 3, errors: 1, warnings: 2 });
  });

  it('чип адреси: прочерк замість відсутнього рядка чи колонки', () => {
    expect(addressChip('R4', 'C3')).toBe('R4 · C3');
    expect(addressChip(null, null)).toBe('— · —');
  });

  it('⛔ null у лічильнику таблиці — «—», не 0', () => {
    expect(countOrDash(null)).toBe('—');
    expect(countOrDash(undefined)).toBe('—');
    expect(countOrDash(0)).toBe('0');
  });
});
