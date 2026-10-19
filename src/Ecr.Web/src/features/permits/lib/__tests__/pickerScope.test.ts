import { describe, expect, it } from 'vitest';
import { canAddValueInline, choicesOf, optionsFor, refCode, scopeOf } from '../pickerScope';

describe('pickerScope: три області списків', () => {
  it('choice — список лише цього типу', () => {
    expect(scopeOf({ type: 'choice' })).toBe('type');
    // `ref` у choice ігнорується: область визначає тип стовпця.
    expect(scopeOf({ type: 'choice', ref: 'Substances' })).toBe('type');
  });

  it('link на таблицю цього перміта — permit, інакше — спільний реєстр', () => {
    expect(scopeOf({ type: 'link', ref: 'StoragePlaces (this permit)' })).toBe('permit');
    expect(scopeOf({ type: 'link', ref: 'storageplaces (This Permit)' })).toBe('permit');
    expect(scopeOf({ type: 'link', ref: 'Substances' })).toBe('shared');
    expect(scopeOf({ type: 'link' })).toBe('shared');
  });

  it('refCode бере перше слово ref', () => {
    expect(refCode({ ref: 'StoragePlaces (this permit)' })).toBe('StoragePlaces');
    expect(refCode({ ref: '  Sites ' })).toBe('Sites');
    expect(refCode({ ref: '' })).toBeNull();
    expect(refCode({})).toBeNull();
  });

  it('choicesOf не вигадує значень', () => {
    expect(choicesOf({ choices: ['A', 'B'] })).toEqual(['A', 'B']);
    expect(choicesOf({})).toEqual([]);
  });

  describe('optionsFor', () => {
    const sources = {
      shared: { Substances: ['Soot', 'Xylene'], Sites: ['Hangar'] },
      permitTables: { StoragePlaces: ['Site 1 · Bolashak', 'Site 1 · Kashagan'] },
    };

    it('список типу — з самого стовпця', () => {
      expect(optionsFor({ type: 'choice', choices: ['Hazardous', 'Non-hazardous'] }, sources)).toEqual([
        { value: 'Hazardous', label: 'Hazardous' },
        { value: 'Non-hazardous', label: 'Non-hazardous' },
      ]);
    });

    it('спільний реєстр береться з shared, а не з permitTables', () => {
      expect(optionsFor({ type: 'link', ref: 'Substances' }, sources).map((o) => o.label)).toEqual(['Soot', 'Xylene']);
      // Реєстр з таким ім'ям є лише в permitTables — для shared його немає.
      expect(optionsFor({ type: 'link', ref: 'StoragePlaces' }, sources)).toEqual([]);
    });

    it('таблиця перміта береться з permitTables, а не з shared', () => {
      expect(optionsFor({ type: 'link', ref: 'StoragePlaces (this permit)' }, sources).map((o) => o.label)).toEqual([
        'Site 1 · Bolashak',
        'Site 1 · Kashagan',
      ]);
      expect(optionsFor({ type: 'link', ref: 'Substances (this permit)' }, sources)).toEqual([]);
    });

    it('невідоме джерело й відсутній ref — порожній список', () => {
      expect(optionsFor({ type: 'link', ref: 'Nope' }, sources)).toEqual([]);
      expect(optionsFor({ type: 'link' }, sources)).toEqual([]);
      expect(optionsFor({ type: 'link', ref: 'Substances' })).toEqual([]);
      expect(optionsFor({ type: 'choice' }, sources)).toEqual([]);
    });

    it('дублікати й порожні підписи відкидаються, порядок зберігається', () => {
      const options = optionsFor({ type: 'choice', choices: ['B', ' ', 'A', 'B', ' A '] }, sources);
      expect(options.map((o) => o.value)).toEqual(['B', 'A']);
    });
  });

  it('значення «на льоту» можна додати у спільний реєстр і список типу, але не в таблицю перміта', () => {
    expect(canAddValueInline('shared')).toBe(true);
    expect(canAddValueInline('type')).toBe(true);
    expect(canAddValueInline('permit')).toBe(false);
  });
});
