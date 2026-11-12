import { describe, it, expect } from 'vitest';
import { calcErrorCause, calcErrorHint, calcErrorOf, type CellCalcError } from '@/features/grid/calcError';

/**
 * ✎ C-1 (D-475): позначка помилки формули в сітці — стан із `slice.calcErrors`, причина й час словами.
 *
 * ⚠ Каталог у тестах не завантажено — `t` дає позначений ключ `⟦ключ⟧`.
 */

const Since = '2026-11-11T10:00:00Z';

describe('calcError (C-1)', () => {
  it('стан комірки береться за ключем «рядок:колонка»; немає — null', () => {
    const error: CellCalcError = { kind: 'Error', errorCode: '#DIV/0', since: Since, source: null };
    const slice = { calcErrors: { 'R1:C': error } };

    expect(calcErrorOf(slice, 'R1', 'C')).toBe(error);
    expect(calcErrorOf(slice, 'R1', 'D')).toBeNull();
    expect(calcErrorOf({ calcErrors: null }, 'R1', 'C')).toBeNull();
  });

  it('причина — за кодом, невідомий код — загальна з кодом', () => {
    expect(calcErrorCause('#DIV/0')).toBe('⟦grid.calcError.cause.divideByZero⟧');
    expect(calcErrorCause('#REF')).toBe('⟦grid.calcError.cause.badReference⟧');
    expect(calcErrorCause('#BUDGET')).toBe('⟦grid.calcError.cause.budget⟧');
    expect(calcErrorCause('#XYZ')).toContain('grid.calcError.cause.other');
  });

  it('власна помилка, «не оновлено» з джерелом і з закритим джерелом — різні підказки', () => {
    expect(calcErrorHint({ kind: 'Error', errorCode: '#DIV/0', since: Since, source: null })).toContain(
      'grid.calcError.error',
    );
    expect(
      calcErrorHint({
        kind: 'Blocked',
        errorCode: '#DIV/0',
        since: Since,
        source: { tableCode: 'T', rowKey: 'R1', columnCode: 'C', periodKey: null },
      }),
    ).toContain('grid.calcError.blocked (source=T · C (R1)');
    expect(calcErrorHint({ kind: 'Blocked', errorCode: '#DIV/0', since: Since, source: null })).toContain(
      'grid.calcError.blockedHidden',
    );
  });
});
