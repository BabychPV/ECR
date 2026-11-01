import { describe, expect, it } from 'vitest';
import {
  compareVersions,
  distinctVersions,
  filterByVersions,
  nextVersionSort,
  parseVersionSort,
  parseVersionsParam,
  resolveTemplateVersion,
  sortByVersion,
  versionsToParam,
} from '@/features/documents/documentVersion';

/**
 * Колонка «Version» переліку документів: версія періоду = `templateVersionByPeriod[період] ?? templateVersion`.
 *
 * ⛔ Мутаційний доказ: у `resolveTemplateVersion` поміняй пріоритет (спершу `templateVersion`, потім карта) -
 * тести «карта перекриває типову» і «сортування за версією періоду» почервоніють.
 */
const doc = (id: number, templateVersion: string | null, byPeriod?: Record<string, string> | null) => ({
  id,
  templateVersion,
  templateVersionByPeriod: byPeriod ?? null,
});

describe('resolveTemplateVersion', () => {
  it('карта періоду перекриває типову версію', () => {
    expect(resolveTemplateVersion(doc(1, '1.0', { '202609': '2.0' }), 202609)).toEqual({
      version: '2.0',
      fromPeriod: true,
    });
  });

  it('період поза картою — типова версія', () => {
    expect(resolveTemplateVersion(doc(1, '1.0', { '202609': '2.0' }), 202610)).toEqual({
      version: '1.0',
      fromPeriod: false,
    });
  });

  it('період не обрано — типова версія, карта не читається', () => {
    expect(resolveTemplateVersion(doc(1, '1.0', { '202609': '2.0' }), null).version).toBe('1.0');
  });

  it('немає ні версії, ні карти (або порожні рядки) — null, а не вигадана версія', () => {
    expect(resolveTemplateVersion(doc(1, null), 202609)).toEqual({ version: null, fromPeriod: false });
    expect(resolveTemplateVersion(doc(1, '  ', { '202609': '' }), 202609).version).toBeNull();
  });

  it('порожнє значення в карті не затирає типову версію', () => {
    expect(resolveTemplateVersion(doc(1, '1.0', { '202609': '' }), 202609).version).toBe('1.0');
  });
});

describe('compareVersions', () => {
  it('числа порівнюються числами: 2.10 > 2.9', () => {
    expect(compareVersions('2.10', '2.9')).toBeGreaterThan(0);
    expect(compareVersions('2.9', '2.10')).toBeLessThan(0);
  });

  it('коротша версія менша за довшу з тим самим початком; префікс v ігнорується', () => {
    expect(compareVersions('2.3', '2.3.1')).toBeLessThan(0);
    expect(compareVersions('v3', '3')).toBe(0);
  });

  it('число раніше за текст', () => {
    expect(compareVersions('1.0', '1.beta')).toBeLessThan(0);
  });
});

describe('sortByVersion', () => {
  const rows = [doc(1, '2.0'), doc(2, '1.0'), doc(3, null), doc(4, '2.0'), doc(5, '10.0'), doc(6, '1.0', { '202609': '3.0' })];

  it('за зростанням: безверсійні в кінці, рівні лишаються в порядку сервера', () => {
    expect(sortByVersion(rows, 'asc', 202610).map((r) => r.id)).toEqual([2, 6, 1, 4, 5, 3]);
  });

  it('за спаданням: безверсійні все одно в кінці, рівні — у порядку сервера', () => {
    expect(sortByVersion(rows, 'desc', 202610).map((r) => r.id)).toEqual([5, 1, 4, 2, 6, 3]);
  });

  it('сортує за версією ОБРАНОГО періоду, а не типовою', () => {
    expect(sortByVersion(rows, 'asc', 202609).map((r) => r.id)).toEqual([2, 1, 4, 6, 5, 3]);
  });

  it('без сортування — той самий масив', () => {
    expect(sortByVersion(rows, null, 202609)).toBe(rows);
  });
});

describe('filterByVersions / distinctVersions', () => {
  const rows = [doc(1, '1.0'), doc(2, '2.0'), doc(3, null), doc(4, '1.0', { '202609': '2.0' })];

  it('точний збіг за версією періоду', () => {
    expect(filterByVersions(rows, ['2.0'], 202609).map((r) => r.id)).toEqual([2, 4]);
    expect(filterByVersions(rows, ['2.0'], 202610).map((r) => r.id)).toEqual([2]);
  });

  it('кілька версій — об’єднання; безверсійний рядок не проходить жодного фільтра', () => {
    expect(filterByVersions(rows, ['1.0', '2.0'], 202610).map((r) => r.id)).toEqual([1, 2, 4]);
  });

  it('порожній вибір — без фільтра (той самий масив)', () => {
    expect(filterByVersions(rows, [], 202609)).toBe(rows);
  });

  it('різні версії сторінки — впорядковані, без дублів і порожніх', () => {
    expect(distinctVersions([doc(1, '2.10'), doc(2, '2.9'), doc(3, '2.9'), doc(4, null)], null)).toEqual(['2.9', '2.10']);
  });
});

describe('адреса: versions / vsort', () => {
  it('versions: розбір відкидає порожнє й дублі, зворотне перетворення порожнього — null', () => {
    expect(parseVersionsParam('1.0, 2.0,,1.0')).toEqual(['1.0', '2.0']);
    expect(parseVersionsParam(null)).toEqual([]);
    expect(versionsToParam([])).toBeNull();
    expect(versionsToParam(['1.0', '2.0'])).toBe('1.0,2.0');
  });

  it('vsort: невідоме — без сортування; цикл asc → desc → без', () => {
    expect(parseVersionSort('asc')).toBe('asc');
    expect(parseVersionSort('zzz')).toBeNull();
    expect(nextVersionSort(null)).toBe('asc');
    expect(nextVersionSort('asc')).toBe('desc');
    expect(nextVersionSort('desc')).toBeNull();
  });
});
