import { afterEach, describe, expect, it } from 'vitest';
import { cleanup } from '@testing-library/react';
import { resetMissingReports } from '@/shared/i18n';
import { DataTable, type DataTableColumn } from '@/shared/ui/DataTable';
import { renderWithMantine } from '@/test/render';
import { formatDecimal } from '../number';

/**
 * ОДНЕ правило подання десяткового рядка — ОДНЕ місце.
 *
 * ⛔ До зведення `formatDecimal` існував у кількох примірниках — канонічний у
 * `shared/format/number.ts` і копії в споживачах. Копія, що поїхала, на своєму
 * екрані виглядає бездоганно; ловить її лише твердження, що порівнює екран із
 * каноном — тобто це.
 *
 * ✎ P-1124-3: другий споживач (модалка рядків зрізу звітності, стеля десять знаків, `D15-09`)
 * прибрано разом зі зрізами; лишається перелік (`DataTable`) і канонічна функція з аргументом стелі.
 */

/**
 * Двадцять значущих цифр: `double` на цьому вході викидає дробову частину ЦІЛКОМ
 * і змінює останню цифру цілої — `String(Number('12345678901234567.125')) ===
 * '12345678901234568'`. На шістнадцяти знаках мутація «пустити через `Number`»
 * лишилася б зеленою.
 */
const Same = '12345678901234567.125';
const SameThroughDouble = '12,345,678,901,234,568';
const SameShown = '12,345,678,901,234,567.125';

/** Шістнадцять дробових знаків: перелік ріже до трьох (дефолт `Intl`). */
const Deep = '1234.1234567890123456';
const DeepInGrid = '1,234.123';

/** Нерозривні пробіли ICU → звичайний: інакше падіння нечитабельне. */
function norm(text: string | null): string | null {
  return text === null ? null : text.replace(/[   ]/g, ' ');
}

interface Reading {
  readonly id: string;
  readonly amount: string;
}

const gridColumns: readonly DataTableColumn<Reading>[] = [{ key: 'amount', label: 'Обсяг', num: true }];

/** Те, що показує числова клітинка переліку для заданого рядка. */
function shownInGrid(value: string): string {
  renderWithMantine(
    <DataTable<Reading>
      columns={gridColumns}
      rows={[{ id: 'a', amount: value }]}
      rowKey={(row) => row.id}
    />,
  );

  const cell = document.querySelector('tbody tr td');

  return norm(cell?.textContent ?? '') ?? '';
}

afterEach(() => {
  cleanup();
  resetMissingReports();
});

describe('замір: чого саме коштує прохід через double', () => {
  it('Number(...) на цьому вході ТАКИ псує значення', () => {
    expect(String(Number(Same))).toBe('12345678901234568');
    expect(String(Number(Deep))).toBe('1234.1234567890124');
  });
});

describe('перелік показує значення через канон, а не через double', () => {
  it('те саме значення в переліку і в канонічній функції показане однаково', () => {
    const inGrid = shownInGrid(Same);

    // ⛔ Мутуйте `MaxIntlFractionDigits` у канонічній функції на `0` — падає і цей рядок.
    expect(inGrid).toBe(norm(formatDecimal(Same, undefined, 3)));
    expect(inGrid).toBe(SameShown);
    expect(inGrid).not.toBe(SameThroughDouble);
  });

  it('глибокий дріб: перелік ріже до трьох знаків', () => {
    expect(shownInGrid(Deep)).toBe(DeepInGrid);
  });

  it('стеля — аргумент канонічної функції, а не друга реалізація', () => {
    expect(norm(formatDecimal(Deep, undefined, 3))).toBe(DeepInGrid);
    expect(norm(formatDecimal(Deep, undefined, 10))).toBe('1,234.123456789');
    // Без стелі канон показує все, що є у значенні, — і це його дефолт.
    expect(norm(formatDecimal(Deep))).toBe('1,234.1234567890123456');
  });
});
