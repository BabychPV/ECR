import { describe, expect, it } from 'vitest';
import type { CellStyleDto, ColumnDto, TableSliceDto } from '@/api/types';
import { gridColumns } from '../DocumentGrid';
import { cellKey } from '../permissions';
import { NoLocalFlags, type LocalCellFlags } from '../cellState';

/**
 * Директива `docs/build/directive-registry-lookup-and-cell-style.md`,
 * Частина B, PR B2: "готово коли" — колонка зі стилем `IsBold=true` показує
 * жирний текст у сітці; та сама комірка, позначена як `dirty`, і далі
 * показує індикатор `dirty` ОДНОЧАСНО з жирним шрифтом.
 */

function column(overrides: Partial<ColumnDto> = {}): ColumnDto {
  return {
    id: 1,
    code: 'C1',
    header: 'Колонка 1',
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
    style: null,
    ...overrides,
  };
}

function boldStyle(): CellStyleDto {
  return {
    isBold: true,
    isItalic: false,
    foregroundArgb: null,
    backgroundArgb: null,
    horizontalAlign: null,
    verticalAlign: null,
    wrapText: false,
  };
}

function slice(overrides: Partial<TableSliceDto> = {}): TableSliceDto {
  return {
    tableInstanceId: 1,
    periodKey: 202609,
    columns: [column()],
    rows: [
      { rowKey: 'R1', ordinal: 1, rowKind: 'Item', label: null, rowVersion: '0x01', cells: {}, isOrphaned: false },
    ],
    cellPermissions: {},
    cellConfirmations: {},
    ...overrides,
  };
}

const noRequiredInput = { blocked: new Map<string, string>(), warning: new Map<string, string>() };

function cellPropsOf(columns: ReturnType<typeof gridColumns>, rowKey: string) {
  const found = columns.find((c) => c.prop === 'C1');
  if (found === undefined) throw new Error('Немає колонки C1');
  if (typeof found.cellProperties !== 'function') throw new Error('cellProperties відсутній');

  return found.cellProperties({ model: { __rowKey: rowKey } } as never) ?? {};
}

describe('gridColumns — стиль колонки в живій сітці (директива registry-lookup / cell-style, PR B2)', () => {
  it('колонка без стилю — cellProperties без style взагалі', () => {
    const columns = gridColumns(slice(), false, NoLocalFlags, {}, noRequiredInput);

    expect(cellPropsOf(columns, 'R1')).not.toHaveProperty('style');
  });

  it('IsBold=true — комірка без жодного іншого стану все одно отримує font-weight: bold', () => {
    const withStyle = slice({ columns: [column({ style: boldStyle() })] });
    const columns = gridColumns(withStyle, false, NoLocalFlags, {}, noRequiredInput);
    const props = cellPropsOf(columns, 'R1');

    expect(props.style).toEqual({ fontWeight: 'bold' });

    // ⚠ Клас БАЗОВИЙ (`ecr-cell`), не порожній рядок: стиль сам по собі не
    // рахується "станом" (`cellStateOf` тут повернув би `null` — жодна
    // комірка не dirty/readOnly/calculated/orphaned/rounded).
    //
    // ✒ `U-05`: поруч стоїть `ecr-cell-numeric` — колонка `Decimal`
    // вирівнюється праворуч. Фону він не задає й зі станами не змагається.
    expect(props.class).toBe('ecr-cell ecr-cell-numeric');
  });

  it('той самий стиль І dirty-стан — ОБИДВА видимі одночасно (клас dirty + font-weight bold)', () => {
    const withStyle = slice({ columns: [column({ style: boldStyle() })] });
    const dirtyFlags: LocalCellFlags = { dirty: new Set([cellKey('R1', 'C1')]), rounded: new Set() };

    const columns = gridColumns(withStyle, false, dirtyFlags, {}, noRequiredInput);
    const props = cellPropsOf(columns, 'R1');

    // ⛔ Мутаційний доказ директиви: "одне не повинно ховати інше". RED було
    // б або відсутній `style` (стиль зник, щойно з'явився стан), або клас
    // без `dirty` (стан зник, щойно з'явився стиль) — до фіксу B2 `style`
    // не рахувався взагалі, і саме перша половина була б RED.
    expect(props.style).toEqual({ fontWeight: 'bold' });
    expect(props.class).toContain('dirty');
    expect(props['data-cell-state']).toBe('dirty');
  });
});
