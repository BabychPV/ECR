import { describe, it, expect, beforeEach } from 'vitest';
import { DefaultColumnWidth, widthsFromEvent } from '../columnWidths';
import { applyDensity, density, rowHeight, setDensity } from '@/shared/theme/preferences';

beforeEach(() => {
  localStorage.clear();
});

// ⚠ Збереження ширин (сервер + кеш, `D-201`) перевіряють
// `features/preferences/__tests__/columnWidthsSync.test.tsx` і
// `DocumentGrid.columnWidths.test.tsx`; тут — лише типова ширина.
describe('Типова ширина колонки (ФВ-14.29)', () => {
  it('ФВ-14.29: без збереженого значення колонка бере додатну типову ширину', () => {
    expect(DefaultColumnWidth).toBeGreaterThan(0);
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

  it('застосовується атрибутом кореня, а не перерендером таблиць', () => {
    applyDensity('comfortable');

    // ⛔ Саме атрибут: щільність зачіпає всі п'ятнадцять подань, і пропустити
    // її в одному означало б, що екран «майже» перемкнувся.
    //
    // ⛔ `UI-03`: тут стояло ще й твердження про ІНЛАЙНОВУ `--ecr-row-height`
    // на `<html>`. Інлайн прибрано навмисно — він вигравав каскад, тобто
    // справжнім джерелом висоти було число з `theme.ts`, а три змінні
    // `tokens.css` лишалися декорацією (прибери їх — не зміниться нічого).
    // Тепер джерело одне: атрибут вибирає набір змінних у `tokens.css`.
    //
    // ⚠ Що ці змінні справді дають різну ОБЧИСЛЕНУ висоту рядка таблиці й
    // сітки — доводять `shared/theme/__tests__/density.test.tsx` і
    // `features/grid/__tests__/density.test.tsx`; тут — лише те, що вибір
    // долітає до кореня документа.
    expect(document.documentElement.dataset['ecrDensity']).toBe('comfortable');

    applyDensity('compact');
    expect(document.documentElement.dataset['ecrDensity']).toBe('compact');
  });
});
