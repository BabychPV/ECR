import { describe, it, expect } from 'vitest';
import type { TableSliceDto } from '@/api/types';
import { isMissingColumns, isSliceEmpty } from '../emptiness';

/**
 * Порожній стан зрізу таблиці (`ФВ-14.21`, директива №09 `W8` п.2, `S-13`).
 *
 * ⛔ Дефект був один рядок: `isEmpty={(loaded) => loaded.columns.length === 0}`
 * — одна умова на ВСІ таблиці. Фіксована таблиця без жодного рядка проходила
 * її як «непорожня» і малювалася звичайною сіткою: заголовки на місці,
 * вводити нема куди, пояснення немає. Оператор бачив робочий екран, який
 * нічого не приймає.
 */
const Slice = (columns: number, rows: number): TableSliceDto => ({
  tableInstanceId: 1,
  periodKey: 202601,
  cellPermissions: {},
  cellConfirmations: {},
  columns: Array.from({ length: columns }, (_, i) => ({
    id: i + 1,
    code: `C${i + 1}`,
    header: `C${i + 1}`,
    dataType: 'Decimal',
    ordinal: i + 1,
    isReadOnly: false,
    isRequired: false,
    displayFormat: null,
    defaultValue: null,
    lookupRegistryDefId: null,
    unitId: null,
    unitSymbol: null,
    precision: null,
    scale: null,
  })),
  rows: Array.from({ length: rows }, (_, i) => ({
    rowKey: `R${i + 1}`,
    ordinal: i + 1,
    rowKind: 'Item',
    label: null,
    rowVersion: 'AAAAAAAAAAA=',
    cells: {},
    isOrphaned: false,
  })),
});

describe('порожнеча зрізу таблиці', () => {
  it('фіксована таблиця з колонками, але без рядків — порожня', () => {
    // ⛔ Головне твердження всього `S-13`: склад рядків фіксованої таблиці
    // задає шаблон, тому нуль рядків — це стан ДАНИХ, який треба назвати
    // словами, а не сітка, у якій «поки що нічого не ввели».
    expect(isSliceEmpty(Slice(3, 0), false)).toBe(true);
  });

  it('динамічна таблиця без рядків — НЕ порожня: з неї починають', () => {
    // ⚠ Інакше порожній стан сховав би кнопку «Додати рядок» — єдину, якою
    // таку таблицю наповнюють.
    expect(isSliceEmpty(Slice(3, 0), true)).toBe(false);
  });

  it('без колонок порожньо в обох випадках', () => {
    expect(isSliceEmpty(Slice(0, 0), true)).toBe(true);
    expect(isSliceEmpty(Slice(0, 5), false)).toBe(true);
  });

  it('заповнена таблиця не порожня', () => {
    expect(isSliceEmpty(Slice(3, 2), false)).toBe(false);
    expect(isSliceEmpty(Slice(3, 2), true)).toBe(false);
  });

  it('причина порожнечі розрізняється: колонки чи рядки', () => {
    // ⚠ Від цього залежить ТЕКСТ: «немає колонок» відсилає адміністратора у
    // версію шаблону, «немає рядків» — у перелік `RowDef` тієї ж таблиці.
    expect(isMissingColumns(Slice(0, 0))).toBe(true);
    expect(isMissingColumns(Slice(3, 0))).toBe(false);
    expect(isMissingColumns(undefined)).toBe(true);
  });
});
