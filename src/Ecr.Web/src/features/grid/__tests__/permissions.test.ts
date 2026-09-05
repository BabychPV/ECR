import { describe, it, expect, vi } from 'vitest';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cellKey, decide, guardOf } from '@/features/grid/permissions';

/** Права по комірках приходять із сервера і показуються, а не вгадуються. */
function column(overrides: Partial<ColumnDto> = {}): ColumnDto {
  return {
    id: 1,
    code: 'C1',
    header: 'Обсяг',
    dataType: 'Decimal',
    ordinal: 1,
    isReadOnly: false,
    isRequired: false,
    displayFormat: null,
    defaultValue: null,
    lookupRegistryDefId: null,
    unitId: null,
    unitSymbol: null,
    ...overrides,
  };
}

function slice(permissions: Record<string, string>, columns: ColumnDto[] = [column()]): TableSliceDto {
  return {
    tableInstanceId: 1,
    periodKey: 202603,
    columns,
    rows: [
      {
        rowKey: 'R1',
        ordinal: 1,
        rowKind: 'Static',
        label: null,
        rowVersion: '0x01',
        cells: {},
        isOrphaned: false,
      },
    ],
    cellPermissions: permissions,
  };
}

describe('Права по комірках', () => {
  it('read-only комірки візуально відрізняються', () => {
    const decision = decide(slice({}), 'R1', column({ isReadOnly: true }));

    expect(decision.editable).toBe(false);
    expect(decision.reason).toBe('ReadOnlyColumn');
  });

  it('підказка показує причину заборони, а не просто «недоступно»', () => {
    // ⚠ Користувач, який бачить сіру комірку без пояснення, іде до
    // адміністратора, а той — до розробника.
    const decision = decide(slice({ [cellKey('R1', 'C1')]: 'PeriodClosed' }), 'R1', column());

    expect(decision.reason).toBe('PeriodClosed');
    expect(decision.hint).toContain('Період закрито');
    expect(decision.hint).not.toBe('недоступно');
  });

  it('редагування забороненої комірки не надсилає запит на сервер', async () => {
    const send = vi.fn();
    const guard = guardOf(slice({ [cellKey('R1', 'C1')]: 'NoPermission' }));

    const reason = guard('R1', 'C1');
    if (reason === null) send();

    expect(reason).not.toBeNull();
    expect(send).not.toHaveBeenCalled();
  });

  it('обчислені комірки не редагуються', () => {
    for (const dataType of ['Formula', 'Calculated']) {
      const decision = decide(slice({}), 'R1', column({ dataType }));

      expect(decision.editable).toBe(false);
      expect(decision.reason).toBe('Calculated');
    }
  });

  it('відсутність запису у словнику прав означає ДОЗВІЛ', () => {
    // Сервер віддає лише відхилення: словник на 500×60 із дозволами на кожну
    // комірку важив би більше за самі дані.
    expect(decide(slice({}), 'R1', column()).editable).toBe(true);
  });

  it('невідома причина від сервера НЕ відкриває редагування', () => {
    // ⛔ Нова причина на сервері інакше відкривала б редагування там, де його
    // щойно заборонили.
    const decision = decide(slice({ [cellKey('R1', 'C1')]: 'СутоНоваПричина' }), 'R1', column());

    expect(decision.editable).toBe(false);
    expect(decision.reason).toBe('NoPermission');
  });

  it('сторож вставки і сіра комірка ніколи не розходяться', () => {
    // Один предикат на обидві поведінки: інакше «комірка сіра» і «сюди не
    // вставиться» відповідали б різними правилами.
    const data = slice({ [cellKey('R1', 'C1')]: 'DocumentSubmitted' });

    expect(guardOf(data)('R1', 'C1')).toBe(decide(data, 'R1', column()).hint);
  });
});
