import { describe, expect, it } from 'vitest';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { gridColumns } from '../DocumentGrid';
import { NoLocalFlags } from '../cellState';

/**
 * Аудит Етапу 3, лана "Documents core"
 * (`lane3-readonly-cell-after-submit-not-communicated.md`): після Submit
 * комірка без власної серверної причини заборони (`decision.hint`
 * порожній) тепер несе ТЕКСТ, а не лише клас/курсор — читалка екрана й
 * наведення миші мають чути, чому клік нічого не робить.
 */

function column(overrides: Partial<ColumnDto> = {}): ColumnDto {
  return {
    id: 1,
    code: 'C1',
    header: 'C1',
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

function titleOf(columns: ReturnType<typeof gridColumns>, rowKey: string): string | undefined {
  const found = columns.find((c) => c.prop === 'C1');
  if (found === undefined || typeof found.cellProperties !== 'function') throw new Error('cellProperties відсутній');

  const props = found.cellProperties({ model: { __rowKey: rowKey } } as never) ?? {};
  return props.title as string | undefined;
}

describe('gridColumns — підказка на комірці, замкненій через readOnly подання (не власним правилом)', () => {
  it('grid readOnly=true, комірку не забороняє жодне власне правило — title несе пояснення', () => {
    const columns = gridColumns(slice(), true, NoLocalFlags, {}, noRequiredInput);

    expect(titleOf(columns, 'R1')).toContain('submittedReadOnlyHint');
  });

  it('grid readOnly=false — жодного такого тексту (звичайна редагована комірка без стану)', () => {
    const columns = gridColumns(slice(), false, NoLocalFlags, {}, noRequiredInput);

    expect(titleOf(columns, 'R1')).toBeUndefined();
  });

  it('колонку забороняє ВЛАСНЕ правило (IsReadOnly) — той самий фолбек НЕ додається поверх серверної причини', () => {
    // ⚠ `readOnly` (грід) тут false: заборона суто структурна
    // (`ColumnDto.isReadOnly`), сервер уже пояснює причину через
    // `decision.hint` — додавати ще й "sheet submitted" було б брехнею.
    const withReadOnlyColumn = slice({ columns: [column({ isReadOnly: true })] });
    const columns = gridColumns(withReadOnlyColumn, false, NoLocalFlags, {}, noRequiredInput);

    expect(titleOf(columns, 'R1')).not.toContain('submittedReadOnlyHint');
  });
});
