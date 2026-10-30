import { describe, expect, it } from 'vitest';
import { isHumanCellChange } from '@/features/audit/api';
import { cellAuthorHint, cellAuthorName } from '@/features/audit/authorOptions';

/**
 * ✎ D-461 (рішення людини 30.10): повернення формули, яке записав ПЕРЕНОС ВЕРСІЇ, — дія системи: автор «Система»,
 * а того, хто запустив перенос, видно в підказці (збережений `changedByUserId` не переписується).
 */
const base = { changedByUserId: 7, changedByDisplayName: 'Olena' };

describe('автор зміни комірки для повернення формули', () => {
  it('FormulaRestored від переносу версії — «Система», ініціатор у підказці', () => {
    const row = { ...base, origin: 'FormulaRestored', reason: 'Version migration' };
    expect(isHumanCellChange(row)).toBe(false);
    expect(cellAuthorName(row)).toMatch(/audit\.systemAuthor/);
    expect(cellAuthorHint(row)).toBe('Olena (#7)');
  });

  it('FormulaRestored людиною (Delete) і FormulaOverride — ім’я людини', () => {
    expect(cellAuthorName({ ...base, origin: 'FormulaRestored', reason: null })).toBe('Olena');
    expect(cellAuthorName({ ...base, origin: 'FormulaOverride', reason: 'Version migration' })).toBe('Olena');
    expect(cellAuthorHint({ ...base, origin: 'FormulaRestored' })).toBe('#7');
  });
});
