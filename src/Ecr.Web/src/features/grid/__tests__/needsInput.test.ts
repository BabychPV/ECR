import { describe, expect, it } from 'vitest';
import type { RequiredInputCell } from '@/api/client';
import { needsInputByColumn, needsInputCells, needsInputHint, needsInputMaps, type NeedsInputSources } from '../needsInput';

/**
 * ✎ DOCUX L2 (§4.1 плану): модель «тут потрібне значення» — шаблон `isRequired` одразу при відкритті, входи методологій
 * (`ECR-CALC-0437`), «Missing input» з переліку проблем; з поточних значень; Q4 — запасні порожні рядки без позначок.
 */
const Columns = [
  { code: 'NAME', isRequired: true },
  { code: 'FUEL', isRequired: false },
  { code: 'QTY', isRequired: false },
];

function sources(over: Partial<NeedsInputSources> = {}): NeedsInputSources {
  return {
    columns: Columns,
    rows: [
      { rowKey: 'fixed', rowKind: 'Item' },
      { rowKey: 'spare', rowKind: 'Dynamic' },
      { rowKey: 'typed', rowKind: 'Dynamic' },
    ],
    shown: [{ __rowKey: 'fixed' }, { __rowKey: 'spare', NAME: '  ' }, { __rowKey: 'typed', QTY: 5 }],
    blocked: [],
    warnings: [],
    issues: [],
    editable: () => true,
    ...over,
  };
}

const keys = (cells: ReturnType<typeof needsInputCells>): string[] =>
  cells.map((cell) => `${cell.rowKey}:${cell.columnCode}:${cell.level}:${cell.source}`);

function inputCell(rowKey: string, columnCode: string, methodologies: string[] = []): RequiredInputCell {
  return { rowKey, columnCode, ruleCode: 'ECR-CALC-0437', message: 'm', methodologies };
}

describe('needsInputCells', () => {
  it('Q4: шаблонна колонка — у наперед заданому рядку і в рядку з хоч одним значенням; запасний порожній — ні', () => {
    expect(keys(needsInputCells(sources()))).toEqual(['fixed:NAME:block:template', 'typed:NAME:block:template']);
  });

  it('значення з поточних даних (незбережена правка) знімає позначку; даних ще немає — нічого', () => {
    const filled = sources({ shown: [{ __rowKey: 'fixed', NAME: 'x' }, { __rowKey: 'spare' }, { __rowKey: 'typed', QTY: 5, NAME: 0 }] });
    expect(needsInputCells(filled)).toHaveLength(0);
    expect(needsInputCells(sources({ shown: null }))).toHaveLength(0);
  });

  it('нередаговна комірка (формула, права, поданий аркуш) — без позначки', () => {
    expect(keys(needsInputCells(sources({ editable: (rowKey) => rowKey !== 'fixed' })))).toEqual(['typed:NAME:block:template']);
  });

  it('входи методологій: Block і Warn з рівнем сервера; заповнена комірка й невідомий рядок випадають', () => {
    const cells = needsInputCells(
      sources({
        columns: [{ code: 'NAME', isRequired: false }, ...Columns.slice(1)],
        blocked: [inputCell('fixed', 'FUEL', ['M1'])],
        warnings: [inputCell('typed', 'FUEL', ['M2']), inputCell('typed', 'QTY'), inputCell('gone', 'FUEL')],
      }),
    );
    expect(keys(cells)).toEqual(['fixed:FUEL:block:methodology', 'typed:FUEL:warn:methodology']);
    expect(cells[0]?.methodologies).toEqual(['M1']);
  });

  it('«Missing input» з переліку проблем — warn; інші види проблем не позначають', () => {
    const cells = needsInputCells(
      sources({
        columns: [{ code: 'NAME', isRequired: false }, ...Columns.slice(1)],
        issues: [
          { rowKey: 'fixed', kind: 'MissingInputs', methodologyCode: 'ECW_1', columns: [{ code: 'FUEL' }, { code: 'QTY' }] },
          { rowKey: 'typed', kind: 'NoMatchingRule', columns: [{ code: 'FUEL' }] },
        ],
      }),
    );
    expect(keys(cells)).toEqual(['fixed:FUEL:warn:calcIssue', 'fixed:QTY:warn:calcIssue']);
  });

  it('та сама комірка з кількох джерел — одна позначка, сильніший рівень, методології злито; порядок — рядок, колонка', () => {
    const cells = needsInputCells(
      sources({
        warnings: [inputCell('fixed', 'NAME', ['M2'])],
        issues: [{ rowKey: 'fixed', kind: 'MissingInputs', methodologyCode: 'M3', columns: [{ code: 'NAME' }, { code: 'FUEL' }] }],
      }),
    );
    expect(keys(cells)).toEqual(['fixed:NAME:block:template', 'fixed:FUEL:warn:calcIssue', 'typed:NAME:block:template']);
    expect(cells[0]?.methodologies).toEqual(['M2', 'M3']);
  });

  it('мапи для сітки й лічильники колонок; пояснення — ключі каталогу за джерелом', () => {
    const cells = needsInputCells(sources({ warnings: [inputCell('typed', 'FUEL', ['M1']), inputCell('fixed', 'QTY')] }));
    const maps = needsInputMaps(cells);
    expect([...maps.blocked.keys()]).toEqual(['fixed:NAME', 'typed:NAME']);
    expect([...maps.warning.keys()]).toEqual(['fixed:QTY', 'typed:FUEL']);
    expect(maps.blocked.get('fixed:NAME')).toContain('grid.needsInput.hintTemplate');
    expect(maps.warning.get('typed:FUEL')).toContain('grid.needsInput.hintMethodology');
    expect(maps.warning.get('fixed:QTY')).toContain('grid.needsInput.hintMethodologyUnnamed');
    expect([...needsInputByColumn(cells)]).toEqual([
      ['NAME', 2],
      ['QTY', 1],
      ['FUEL', 1],
    ]);
    expect(needsInputHint(cells[0]!)).toContain('hintTemplate');
  });
});
