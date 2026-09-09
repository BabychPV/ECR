import { describe, it, expect } from 'vitest';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { captureEdit, coerce } from '@/features/grid/edits';
import { cellKey } from '@/features/grid/permissions';

/**
 * Захоплення правки з grid.
 *
 * ⚠ Саме тут був дефект, знайдений аудитом Етапу 6: обробник редагування не
 * був підключений узагалі, і grid показував введене значення, **не
 * зберігаючи його**. Таблиця виглядала заповненою, доки її не перевідкриють —
 * і жодної ознаки збою.
 */
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

function slice(
  columns: ColumnDto[] = [column()],
  permissions: Record<string, string> = {},
): TableSliceDto {
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
        rowVersion: '0x2A',
        cells: { C1: 10 },
        isOrphaned: false,
      },
    ],
    cellPermissions: permissions,
    cellConfirmations: {},
  };
}

describe('Правка в grid', () => {
  it('ФВ-3.6: введене значення потрапляє в збереження разом із версією рядка', () => {
    const captured = captureEdit(slice(), { columnCode: 'C1', rowKey: 'R1', raw: '12,5' });

    expect(captured).not.toBeNull();
    expect(captured?.pending.value).toBe(12.5);

    // ⚠ Без baseVersion наступний патч отримав би 409 на власних змінах —
    // конфлікт із самим собою, який неможливо пояснити користувачеві.
    expect(captured?.pending.baseVersion).toBe('0x2A');
  });

  it('крок історії пам’ятає попереднє значення', () => {
    const captured = captureEdit(slice(), { columnCode: 'C1', rowKey: 'R1', raw: '99' });

    expect(captured?.step).toEqual({ rowKey: 'R1', columnCode: 'C1', before: 10, after: 99 });
  });

  it('правка забороненої комірки не захоплюється', () => {
    // ⛔ Право перевіряється ще раз: fill-handle і програмна правка проходять
    // повз редактор, який заборонену комірку не відкриває.
    const data = slice([column()], { [cellKey('R1', 'C1')]: 'PeriodClosed' });

    expect(captureEdit(data, { columnCode: 'C1', rowKey: 'R1', raw: '1' })).toBeNull();
  });

  it('правка обчисленої комірки не захоплюється', () => {
    const data = slice([column({ dataType: 'Formula' })]);

    expect(captureEdit(data, { columnCode: 'C1', rowKey: 'R1', raw: '1' })).toBeNull();
  });

  it('невідомі колонка чи рядок дають відмову, а не порожню правку', () => {
    expect(captureEdit(slice(), { columnCode: 'НЕМА', rowKey: 'R1', raw: '1' })).toBeNull();
    expect(captureEdit(slice(), { columnCode: 'C1', rowKey: 'НЕМА', raw: '1' })).toBeNull();
    expect(captureEdit(slice(), { columnCode: '', rowKey: '', raw: '1' })).toBeNull();
  });

  it('нерозпізнане число лишається текстом, а не стає нулем', () => {
    // Нуль у звіті читається як вимірювання; сервер відповість
    // ECR-CELL-0422 із назвою колонки, і це чесніше.
    expect(coerce('н/д', 'Decimal')).toBe('н/д');
    expect(coerce('', 'Decimal')).toBeNull();
  });

  it('порожнє булеве не стає «ні»', () => {
    // «Не заповнювали» і «ні» — різні стани, і зводити перше до другого
    // означає вигадати відповідь за користувача.
    expect(coerce('', 'Bool')).toBeNull();
    expect(coerce('так', 'Bool')).toBe(true);
    expect(coerce('false', 'Bool')).toBe(false);
  });
});
