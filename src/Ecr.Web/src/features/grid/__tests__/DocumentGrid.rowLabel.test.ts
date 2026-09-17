import { describe, expect, it } from 'vitest';
import type { TableSliceDto } from '@/api/types';
import { NoLocalFlags } from '../cellState';
import { gridColumns } from '../DocumentGrid';

/**
 * Підпис рядка видно в сітці документа.
 *
 * ⛔ Дефект, який це закриває: сервер РАХУЄ Й ЛОКАЛІЗУЄ підпис
 * (`GetTableSliceHandler`: `Label: r.Def?.LabelL10n.Get(language)`) і кладе
 * його в контракт (`TableSliceDto.Label`) — а сітка документа не читала його
 * ЖОДНОГО РАЗУ (`git grep "\.label"` по `features/grid` не давав нічого).
 * Для державної форми з фіксованими рядками це означає таблицю, у якій рядки
 * нічим не відрізняються: стовпчик чисел, і жодної ознаки, котре з них викиди,
 * а котре витрата палива. Показував підпис лише адміністративний екран версії
 * шаблону — той, куди оператор не заходить.
 *
 * ⚠ Перевіряється `gridColumns`, а не змонтована сітка: саме ця функція
 * вирішує, що потрапить у розмітку, і твердження про неї не залежить від
 * того, чи вміє jsdom розкласти віртуалізовану таблицю.
 */

/** Зріз із двома рядками; підписи задає викликач. */
function slice(labels: (string | null)[]): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId: 1,
    columns: [
      {
        code: 'C1',
        dataType: 'Decimal',
        defaultValue: null,
        displayFormat: null,
        header: 'C1',
        id: 1,
        isReadOnly: false,
        isRequired: false,
        isRequiredByMethodology: false,
        lookupRegistryDefId: null,
        ordinal: 0,
        unitId: null,
        unitSymbol: null,
      },
    ],
    rows: labels.map((label, index) => ({
      cells: { C1: index },
      isOrphaned: false,
      label,
      ordinal: index,
      rowKey: `r${index + 1}`,
      rowKind: 'Item',
      rowVersion: 'v1',
    })),
  };
}

/** Виклик із дефолтами, як це робить сам компонент. */
function columnsOf(dto: TableSliceDto) {
  return gridColumns(dto, false, NoLocalFlags, {}, { blocked: new Map(), warning: new Map() });
}

describe('підпис рядка в сітці', () => {
  it('фіксовані рядки дістають ПЕРШУ колонку з підписом', () => {
    const columns = columnsOf(slice(['Валові викиди', 'Витрата палива']));

    // ⛔ Саме перша: підпис ідентифікує рядок, і місце ідентифікатора там, де
    // око починає читати. Після неї — колонки даних, у своєму порядку.
    expect(columns[0]?.prop).toBe('__rowLabel');
    expect(columns[1]?.prop).toBe('C1');
  });

  it('колонка підпису тільки для читання', () => {
    const columns = columnsOf(slice(['Валові викиди', null]));

    // ⚠ Це не дані документа, а опис структури: правити підпис можна рівно
    // там, де його заведено, — у версії шаблону.
    expect(columns[0]?.readonly).toBe(true);
  });

  it('таблиця без жодного підпису колонки не дістає', () => {
    const columns = columnsOf(slice([null, null]));

    // ⛔ Динамічні рядки підписів не мають за побудовою. Колонка з самими
    // технічними ключами відбирала б ширину в даних, нічого не пояснюючи.
    expect(columns).toHaveLength(1);
    expect(columns[0]?.prop).toBe('C1');
  });
});
