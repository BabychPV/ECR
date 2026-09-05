import { describe, it, expect, beforeEach } from 'vitest';
import {
  DefaultColumnWidth,
  readWidths,
  saveWidths,
  widthsFromEvent,
} from '../columnWidths';
import { applyDensity, density, rowHeight, setDensity } from '@/shared/theme/preferences';

beforeEach(() => {
  localStorage.clear();
});

describe('Ширини колонок переживають перезавантаження (ФВ-14.29)', () => {
  it('ФВ-14.29: без збереженого значення ширини немає — колонка бере типову', () => {
    expect(readWidths(1)).toEqual({});
    expect(DefaultColumnWidth).toBeGreaterThan(0);
  });

  it('збережене читається назад', () => {
    saveWidths(1, { volume: 220 });

    expect(readWidths(1)).toEqual({ volume: 220 });
  });

  it('нова ширина дописується, а не затирає решту', () => {
    saveWidths(1, { volume: 220 });
    saveWidths(1, { unit: 90 });

    // ⛔ Саме дописується: RevoGrid віддає в події ЛИШЕ змінену колонку, і
    // заміна цілком стерла б усі попередні підгонки одним перетягуванням.
    expect(readWidths(1)).toEqual({ volume: 220, unit: 90 });
  });

  it('ширини різних таблиць не змішуються', () => {
    saveWidths(1, { volume: 220 });
    saveWidths(2, { volume: 90 });

    expect(readWidths(1)).toEqual({ volume: 220 });
    expect(readWidths(2)).toEqual({ volume: 90 });
  });

  it('пошкоджений вміст трактується як відсутній, а не ламає таблицю', () => {
    localStorage.setItem('ecr.columnWidths:1', '{не json');

    expect(readWidths(1)).toEqual({});
  });

  it('нечислові й недодатні значення відкидаються', () => {
    localStorage.setItem(
      'ecr.columnWidths:1',
      JSON.stringify({ ok: 120, text: 'широка', zero: 0, negative: -5 }),
    );

    // ⚠ Ширина `0` сховала б колонку назавжди, і повернути її користувач не
    // зміг би: перетягувати нема за що.
    expect(readWidths(1)).toEqual({ ok: 120 });
  });
});

describe('Розбір події зміни ширини', () => {
  it('бере код колонки і розмір', () => {
    expect(widthsFromEvent({ 0: { prop: 'volume', size: 200 } })).toEqual({ volume: 200 });
  });

  it('ігнорує колонки без коду або без розміру', () => {
    expect(widthsFromEvent({ 0: { prop: 'volume' }, 1: { size: 100 }, 2: null })).toEqual({});
  });

  it('не падає на несподіваній формі події', () => {
    // ⛔ Подія типізована як довільний словник: помилка тут мовчазна —
    // ширини просто не зберігаються, і причину не знайде ніхто.
    expect(widthsFromEvent(undefined)).toEqual({});
    expect(widthsFromEvent('рядок')).toEqual({});
  });
});

describe('Щільність переживає перезавантаження (ФВ-14.14)', () => {
  it('ФВ-14.14: за замовчуванням щільна', () => {
    expect(density()).toBe('compact');
  });

  it('вибір зберігається', () => {
    setDensity('comfortable');

    expect(density()).toBe('comfortable');
    expect(rowHeight('comfortable')).toBeGreaterThan(rowHeight('compact'));
  });

  it('застосовується CSS-змінною, а не перерендером таблиць', () => {
    applyDensity('comfortable');

    // ⛔ Саме змінна: щільність зачіпає всі п'ятнадцять подань, і пропустити
    // її в одному означало б, що екран «майже» перемкнувся.
    expect(document.documentElement.style.getPropertyValue('--ecr-row-height')).toBe(
      `${rowHeight('comfortable')}px`,
    );
    expect(document.documentElement.dataset['ecrDensity']).toBe('comfortable');
  });
});
