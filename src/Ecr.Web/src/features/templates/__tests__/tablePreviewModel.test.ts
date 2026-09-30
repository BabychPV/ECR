import { describe, expect, it } from 'vitest';
import type { TableDto } from '@/api/types';
import { themeSurface } from '@/shared/theme/theme';
import type { ConditionalFormatRuleDto } from '../conditionalFormatApi';
import { buildTablePreview, cellLook, MaxPreviewRows, previewCellLook } from '../tablePreviewModel';
import { ruleFromWire } from '../conditionalFormat';

/**
 * Модель попереднього перегляду таблиці шаблону (`ФВ-2.6`).
 *
 * Доказ мутацією (перевірено руками, кожна мутація червонить щонайменше один
 * тест нижче): прибрати сортування колонок за `ordinal`; прибрати фільтр
 * `isHidden`; не відкидати неповні правила; рахувати в `ignoredRules` правила
 * інших таблиць; прибрати межу циклу в `depthOf`; прибрати `slice` рядків;
 * у `cellLook` брати колір автора без перевірки контрасту.
 */

type Column = TableDto['columns'][number];
type Row = TableDto['rows'][number];

function column(code: string, ordinal: number, extra: Partial<Column> = {}): Column {
  return {
    code,
    dataType: 'Decimal',
    formulaDialect: null,
    formulaExpression: null,
    headerL10n: { values: { en: `Header ${code}` } },
    displayFormat: null,
    id: ordinal + 1,
    isHidden: false,
    isReadOnly: false,
    isRequired: false,
    ordinal,
    unitSymbol: null,
    ...extra,
  } as Column;
}

function row(rowKey: string, ordinal: number, parentRowKey: string | null = null): Row {
  return {
    formulaDialect: null,
    formulaExpression: null,
    id: ordinal + 1,
    isReadOnly: false,
    label: `Label ${rowKey}`,
    ordinal,
    parentRowKey,
    rowKey,
    rowKind: 'Item',
  } as Row;
}

function rule(columnCode: string, extra: Partial<ConditionalFormatRuleDto> = {}): ConditionalFormatRuleDto {
  return {
    columnCode,
    operator: 'gt',
    value: '10',
    valueTo: null,
    backgroundHex: '#ffcccc',
    foregroundHex: null,
    isBold: false,
    ...extra,
  };
}

const label = (l10n: Column['headerL10n']): string => l10n.values?.['en'] ?? '';

describe('buildTablePreview', () => {
  it('колонки йдуть за ordinal, а не за порядком у відповіді; приховані не показуються, а злічуються', () => {
    const model = buildTablePreview(
      {
        columns: [column('C', 2), column('A', 0), column('H', 1, { isHidden: true }), column('B', 1)],
        rows: [],
      },
      [],
      label,
    );

    expect(model.columns.map((c) => c.code)).toEqual(['A', 'B', 'C']);
    expect(model.hiddenColumns).toBe(1);
    expect(model.columns[0]?.label).toBe('Header A');
  });

  it('підпис колонки без перекладу — її код', () => {
    const model = buildTablePreview({ columns: [column('X', 0, { headerL10n: { values: {} } })], rows: [] }, [], label);

    expect(model.columns[0]?.label).toBe('X');
  });

  it('тип і одиниця колонки переносяться як є', () => {
    const model = buildTablePreview(
      { columns: [column('A', 0, { dataType: 'Int', unitSymbol: 't', isRequired: true, isReadOnly: true })], rows: [] },
      [],
      label,
    );

    expect(model.columns[0]).toMatchObject({ dataType: 'Int', unitSymbol: 't', isRequired: true, isReadOnly: true });
  });

  it('правила лягають на свою колонку в порядку пріоритету; неповні й невідомі — поза переглядом', () => {
    const model = buildTablePreview(
      { columns: [column('A', 0), column('B', 1), column('H', 2, { isHidden: true })], rows: [] },
      [
        rule('A', { value: '100', backgroundHex: '#ff0000' }),
        rule('B'),
        rule('A', { value: '5', backgroundHex: '#00ff00' }),
        rule('A', { value: '' }), // неповне: немає операнда
        rule('A', { operator: 'matches' }), // невідомий оператор
        rule('H'), // прихована колонка
        rule('OTHER_TABLE'), // колонка іншої таблиці версії
      ],
      label,
    );

    expect(model.columns[0]?.rules.map((r) => r.value)).toEqual(['100', '5']);
    expect(model.columns[1]?.rules).toHaveLength(1);
    // Три правила цієї таблиці не показано; правило іншої таблиці не рахується.
    expect(model.ignoredRules).toBe(3);
  });

  it('рядки йдуть за ordinal; вкладеність — за parentRowKey', () => {
    const model = buildTablePreview(
      { columns: [column('A', 0)], rows: [row('child', 1, 'group'), row('group', 0), row('grand', 2, 'child')] },
      [],
      label,
    );

    expect(model.rows.map((r) => [r.rowKey, r.depth])).toEqual([
      ['group', 0],
      ['child', 1],
      ['grand', 2],
    ]);
  });

  it('цикл у parentRowKey не зависає: глибина обмежена кількістю рядків', () => {
    const model = buildTablePreview(
      { columns: [column('A', 0)], rows: [row('a', 0, 'b'), row('b', 1, 'a')] },
      [],
      label,
    );

    expect(model.rows.every((r) => r.depth <= 2)).toBe(true);
  });

  it('рядок без підпису показується ключем', () => {
    const model = buildTablePreview({ columns: [], rows: [{ ...row('R1', 0), label: '  ' }] }, [], label);

    expect(model.rows[0]?.label).toBe('R1');
  });

  it(`більше ${String(MaxPreviewRows)} рядків — показано межу, решту злічено`, () => {
    const rows = Array.from({ length: MaxPreviewRows + 7 }, (_, i) => row(`R${String(i)}`, i));
    const model = buildTablePreview({ columns: [column('A', 0)], rows }, [], label);

    expect(model.rows).toHaveLength(MaxPreviewRows);
    expect(model.truncatedRows).toBe(7);
  });
});

describe('правила з відповіді сервера (через спільний ruleFromWire)', () => {
  it('null у полях — порожньо (правило лишається в колонці); невідомий оператор — не показується', () => {
    const [col] = buildTablePreview(
      { columns: [column('A', 0)], rows: [] },
      [rule('A', { backgroundHex: null, foregroundHex: '#000000', value: null, operator: 'empty' })],
      label,
    ).columns;
    expect(col?.rules).toEqual([ruleFromWire(rule('A', { backgroundHex: null, foregroundHex: '#000000', value: null, operator: 'empty' }))]);
    expect(col?.rules[0]).toMatchObject({ value: '', valueTo: '', backgroundHex: '', foregroundHex: '#000000', isBold: false });

    const unknown = buildTablePreview({ columns: [column('A', 0)], rows: [] }, [rule('A', { operator: 'contains' })], label);
    expect(unknown.columns[0]?.rules).toEqual([]);
    expect(unknown.ignoredRules).toBe(1);
  });
});

describe('cellLook / previewCellLook', () => {
  const light = themeSurface.light.body;
  const dark = themeSurface.dark.body;

  it('без правила — жодного стилю', () => {
    expect(cellLook(null, light)).toEqual({});
  });

  it('заливка + колір автора, що читається на ній; жирність', () => {
    const look = cellLook(ruleFromWire(rule('A', { backgroundHex: '#ffff00', foregroundHex: '#000080', isBold: true })), light);

    expect(look).toEqual({ backgroundColor: '#ffff00', color: '#000080', fontWeight: 'bold' });
  });

  it('колір автора, що не читається на заливці, замінюється текстом теми', () => {
    const look = cellLook(ruleFromWire(rule('A', { backgroundHex: '#ffff00', foregroundHex: '#ffffcc' })), light);

    expect(look.color).toBe(themeSurface.light.text);
  });

  it('без заливки темний колір автора не ставиться на темну поверхню', () => {
    const noFill = ruleFromWire(rule('A', { backgroundHex: null, foregroundHex: '#1a1a1a', isBold: true }));

    expect(cellLook(noFill, light).color).toBe('#1a1a1a');
    expect(cellLook(noFill, dark).color).toBeUndefined();
  });

  it('значення-приклад фарбується першим правилом колонки, що спрацювало', () => {
    const [col] = buildTablePreview(
      { columns: [column('A', 0)], rows: [] },
      [
        rule('A', { value: '100', backgroundHex: '#ff0000' }),
        rule('A', { value: '5', backgroundHex: '#00ff00' }),
        rule('A', { operator: 'empty', value: null, backgroundHex: '#cccccc' }),
      ],
      label,
    ).columns;

    expect(previewCellLook(col!, '150', light).backgroundColor).toBe('#ff0000');
    expect(previewCellLook(col!, '50', light).backgroundColor).toBe('#00ff00');
    expect(previewCellLook(col!, '1', light)).toEqual({});
    expect(previewCellLook(col!, null, light).backgroundColor).toBe('#cccccc');
  });
});
