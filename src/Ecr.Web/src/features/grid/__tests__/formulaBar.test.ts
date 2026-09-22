import { describe, expect, it } from 'vitest';
import type { ColumnDto, RowDto, TableSliceDto } from '@/api/types';
import { formulaBarModel } from '../formulaBar';

/**
 * Рядок формули (`UI-08`): що саме показується для комірки під курсором.
 *
 * ⛔ Чому вираз ПЕРЕДАЄТЬСЯ ззовні, а не береться зі зрізу — у шапці
 * `formulaBar.ts`: `ColumnDto` поля виразу не має, а єдиний маршрут, що віддає
 * `FormulaDto.expression`, — це відповідь на `PUT` версії шаблону. Тому тут
 * перевіряються обидві гілки: вираз відомий і вираз невідомий.
 *
 * ⚠ Індекс колонки береться з масиву `columns`, який отримала сітка, — саме
 * ним RevoGrid і нумерує колонки. Колонка підпису рядків зсуває решту на
 * одиницю, і це перевірено окремим твердженням: без такої перевірки рядок
 * формули показував би сусідню колонку рівно на тих таблицях, де підписи є,
 * тобто на всіх формах із фіксованими рядками.
 */

function column(overrides: Partial<ColumnDto> = {}): ColumnDto {
  return {
    id: 1,
    code: 'C1',
    header: 'Викиди',
    dataType: 'Decimal',
    ordinal: 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    displayFormat: null,
    defaultValue: null,
    lookupRegistryDefId: null,
    unitId: null,
    unitSymbol: null,
    ...overrides,
  };
}

function row(rowKey: string, cells: Record<string, unknown>, label: string | null = null): RowDto {
  return { rowKey, ordinal: 1, rowKind: 'Item', label, rowVersion: '0x01', cells, isOrphaned: false };
}

const Columns = [column({ id: 1, code: 'C1' }), column({ id: 2, code: 'F1', dataType: 'Formula', unitSymbol: 'т' })];

const Slice: TableSliceDto = {
  tableInstanceId: 1,
  periodKey: 202609,
  columns: Columns,
  rows: [row('r1', { C1: '5.0000000000', F1: '12.5' }, 'Діоксид вуглецю')],
  cellPermissions: {},
  cellConfirmations: {},
};

/** Колонки, як їх бачить сітка: службова колонка підпису перша. */
const GridColumns = [{ prop: '__rowLabel' }, { prop: 'C1' }, { prop: 'F1' }];

/** Модель рядка, як її будує `gridRows`. */
const GridRows = [{ __rowKey: 'r1', __rowLabel: 'Діоксид вуглецю', C1: '5.0000000000', F1: '12.5' }];

function ask(
  focus: { rowIndex: number; columnIndex: number } | null,
  extra: Partial<Parameters<typeof formulaBarModel>[0]> = {},
): ReturnType<typeof formulaBarModel> {
  return formulaBarModel({
    slice: Slice,
    columns: GridColumns,
    rows: GridRows,
    labelProp: '__rowLabel',
    focus,
    ...extra,
  });
}

describe('formulaBarModel: комірка під курсором', () => {
  it('без фокуса — нічого: курсора в сітку ще не ставили', () => {
    expect(ask(null)).toBeNull();
  });

  it('колонка підпису рядків комірки не має — рядок формули мовчить', () => {
    // ⚠ Службова колонка не входить у `slice.columns`, тож її `prop` не має
    // коду; показувати для неї «значення» означало б назвати підпис даними.
    expect(ask({ rowIndex: 0, columnIndex: 0 })).toBeNull();
  });

  it('зсув колонки підпису враховано: індекс 1 — це ПЕРША колонка зрізу', () => {
    /*
     * ⛔ Мутація, яку це ловить: `slice.columns[focus.columnIndex]` замість
     * пошуку за `columns[i].prop`. Тоді індекс 1 дав би `F1` — сусідню
     * колонку, — і рядок формули підписував би значення `C1` заголовком
     * «Викиди, т» обчислюваної колонки.
     */
    expect(ask({ rowIndex: 0, columnIndex: 1 })?.columnCode).toBe('C1');
    expect(ask({ rowIndex: 0, columnIndex: 2 })?.columnCode).toBe('F1');
  });

  it('фокус поза межами зрізу — нічого, а не перша комірка', () => {
    expect(ask({ rowIndex: 9, columnIndex: 1 })).toBeNull();
    expect(ask({ rowIndex: 0, columnIndex: 9 })).toBeNull();
  });

  it('звичайна комірка: адреса, значення і жодної ознаки обчислюваності', () => {
    const model = ask({ rowIndex: 0, columnIndex: 1 });

    expect(model?.rowLabel).toBe('Діоксид вуглецю');
    expect(model?.columnHeader).toBe('Викиди');
    expect(model?.isCalculated).toBe(false);
    expect(model?.expression).toBeNull();

    // ⛔ `cellText`, а не `cellDisplay`: рядок формули показує ЗАПИС
    // значення — без групування розрядів і без хвостових нулів масштабу.
    expect(model?.value).toBe('5');
  });

  it('обчислювана комірка з ВІДОМИМ виразом показує сам вираз', () => {
    /*
     * ⛔ Це і є мутаційний доказ вимоги «показати саму формулу»: прибери
     * `expression` з відповіді (чи не передай мапу далі) — і твердження
     * падає з `expected null to be 'SUM(C1:C10) * CST.GWP_CO2'`.
     */
    const model = ask(
      { rowIndex: 0, columnIndex: 2 },
      { expressionByColumnCode: new Map([['F1', 'SUM(C1:C10) * CST.GWP_CO2']]) },
    );

    expect(model?.isCalculated).toBe(true);
    expect(model?.expression).toBe('SUM(C1:C10) * CST.GWP_CO2');
  });

  it('вираз береться за КОДОМ колонки, а не «перший-ліпший» із мапи', () => {
    const model = ask({ rowIndex: 0, columnIndex: 2 }, { expressionByColumnCode: new Map([['C1', 'НЕ ТОЙ']]) });

    expect(model?.expression).toBeNull();
  });

  it('обчислювана комірка без виразу лишається обчислюваною — це різні факти', () => {
    // ⚠ «Виразу не знаємо» не означає «комірка звичайна»: саме на цій
    // різниці тримається напис `grid.formulaBarNoExpression`.
    const model = ask({ rowIndex: 0, columnIndex: 2 });

    expect(model?.isCalculated).toBe(true);
    expect(model?.expression).toBeNull();
    expect(model?.unitSymbol).toBe('т');
  });

  it('`Calculated` (вихід методології) — така сама обчислювана комірка, як `Formula`', () => {
    const calculated: TableSliceDto = { ...Slice, columns: [column({ code: 'C1', dataType: 'Calculated' })] };

    const model = formulaBarModel({
      slice: calculated,
      columns: [{ prop: 'C1' }],
      rows: GridRows,
      labelProp: '__rowLabel',
      focus: { rowIndex: 0, columnIndex: 0 },
    });

    expect(model?.isCalculated).toBe(true);
  });

  it('незбережена правка перекриває значення моделі', () => {
    const model = ask(
      { rowIndex: 0, columnIndex: 1 },
      { pending: new Map([['r1:C1', { rowKey: 'r1', columnCode: 'C1', value: '77' }]]) },
    );

    expect(model?.value).toBe('77');
  });

  it('порожня комірка дає порожнє значення, а не `0`', () => {
    const empty: TableSliceDto = { ...Slice, rows: [row('r1', {}, 'Діоксид вуглецю')] };

    const model = formulaBarModel({
      slice: empty,
      columns: GridColumns,
      rows: [{ __rowKey: 'r1', __rowLabel: 'Діоксид вуглецю', C1: '', F1: '' }],
      labelProp: '__rowLabel',
      focus: { rowIndex: 0, columnIndex: 1 },
    });

    expect(model?.value).toBe('');
  });

  it('рядок без підпису адресується технічним ключем, а не порожнечею', () => {
    const unlabelled: TableSliceDto = { ...Slice, rows: [row('r1', { C1: '1' })] };

    const model = formulaBarModel({
      slice: unlabelled,
      columns: [{ prop: 'C1' }],
      rows: [{ __rowKey: 'r1', C1: '1' }],
      labelProp: '__rowLabel',
      focus: { rowIndex: 0, columnIndex: 0 },
    });

    expect(model?.rowLabel).toBe('r1');
  });
});
