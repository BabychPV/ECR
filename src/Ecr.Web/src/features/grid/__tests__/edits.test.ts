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
    isRequiredByMethodology: false,
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

    // ✎ 2026-09-21: було `toBe(12.5)`. Після `e470777a` `decimal` їде на
    // сервер РЯДКОМ — `Number` на цьому шляху губив 16-й знак
    // (`String(Number('1234.1234567890123456'))` дає `'1234.1234567890124'`).
    // Кома вводу нормалізується, як і доти.
    expect(captured?.pending.value).toBe('12.5');

    // ⚠ Без baseVersion наступний патч отримав би 409 на власних змінах —
    // конфлікт із самим собою, який неможливо пояснити користувачеві.
    expect(captured?.pending.baseVersion).toBe('0x2A');
  });

  it('крок історії пам’ятає попереднє значення', () => {
    const captured = captureEdit(slice(), { columnCode: 'C1', rowKey: 'R1', raw: '99' });

    // ⚠ `before` лишається числом: це те, що лежало у зрізі-фікстурі. `after`
    // — рядок, бо саме рядком нове значення поїде на сервер (`e470777a`).
    expect(captured?.step).toEqual({ rowKey: 'R1', columnCode: 'C1', before: 10, after: '99' });
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

  it('правка, що не змінює значення, правкою не є', () => {
    // ⛔ `e470777a`: сервер віддає значення в масштабі колонки, оператор бачить
    // `10` і набирає `10`, а RevoGrid повідомляє `afteredit` на кожен вихід із
    // редактора. Текстове порівняння назвало б це правкою, і комірка дістала б
    // позначку незбереженої на порожньому місці.
    expect(captureEdit(slice(), { columnCode: 'C1', rowKey: 'R1', raw: '10' })).toBeNull();
    expect(captureEdit(slice(), { columnCode: 'C1', rowKey: 'R1', raw: '10.0000000000' })).toBeNull();
  });

  it('порожнє булеве не стає «ні»', () => {
    // «Не заповнювали» і «ні» — різні стани, і зводити перше до другого
    // означає вигадати відповідь за користувача.
    expect(coerce('', 'Bool')).toBeNull();
    expect(coerce('так', 'Bool')).toBe(true);
    expect(coerce('false', 'Bool')).toBe(false);
  });

  // ⛔ Директива registry-lookup, PR A4: `LookupCellEditor` завжди шле сюди
  // рядкове представлення `entry.Id` (`String(entryId)`) або порожній рядок
  // (скасовано вибір) — НІКОЛИ текст показу (`entry.Display`). До цієї гілки
  // `coerce()` не мав окремого випадку для `'Lookup'` і провалювався в
  // `return raw`, тобто зберігав би ДЕСЯТКОВЕ ЧИСЛО як РЯДОК — і
  // `CellValueReader.Read` (бекенд) розібрав би `'3'`-рядок у
  // `ValueRegistryEntryId` коректно лише випадково, через `int.TryParse`
  // фолбек, а результат все одно НЕ number на клієнті (порушує контракт
  // `PatchCellsRequest.PatchCell.Value: object`, де Lookup-комірки мають
  // нести число — `directive-registry-lookup-and-cell-style.md`, PR A4,
  // «збереження — ValueRegistryEntryId (число), не текст»).
  it('Lookup: рядкове представлення entryId стає числом', () => {
    expect(coerce('3', 'Lookup')).toBe(3);
    expect(coerce('42', 'Lookup')).toBe(42);
  });

  it('Lookup: порожній вибір (скасування) — null, не 0 і не порожній рядок', () => {
    expect(coerce('', 'Lookup')).toBeNull();
    expect(coerce('   ', 'Lookup')).toBeNull();
  });

  it('Lookup: нерозпізнаний ідентифікатор лишається текстом (сервер відповість ECR-CELL-0422)', () => {
    expect(coerce('н/д', 'Lookup')).toBe('н/д');
  });
});
