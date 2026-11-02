import { describe, expect, it } from 'vitest';
import {
  compareVersions,
  distinctVersions,
  mergeVersionOptions,
  nextVersionSort,
  parseVersionSort,
  parseVersionsParam,
  resolveTemplateVersion,
  versionSortParam,
  versionsToParam,
} from '@/features/documents/documentVersion';

/**
 * Колонка «Version» переліку документів: версія періоду = `templateVersionByPeriod[період] ?? templateVersion`.
 *
 * ⛔ Мутаційний доказ: у `resolveTemplateVersion` поміняй пріоритет (спершу `templateVersion`, потім карта) -
 * тест «карта перекриває типову» почервоніє. (Сортування й фільтр версії тепер серверні: `DocumentTemplateVersionListApiTests`.)
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

describe('серверні параметри колонки «Version»', () => {
  it('versionSortParam: asc → templateVersion, desc → -templateVersion, без сортування — null', () => {
    expect(versionSortParam('asc')).toBe('templateVersion');
    expect(versionSortParam('desc')).toBe('-templateVersion');
    expect(versionSortParam(null)).toBeNull();
  });

  it('distinctVersions: різні версії сторінки — впорядковані, без дублів і порожніх', () => {
    expect(distinctVersions([doc(1, '2.10'), doc(2, '2.9'), doc(3, '2.9'), doc(4, null)], null)).toEqual(['2.9', '2.10']);
  });

  it('mergeVersionOptions: обрані версії, яких немає на сторінці, лишаються у виборі (їх є чим зняти)', () => {
    expect(mergeVersionOptions(['2.9'], ['2.10', '2.9'])).toEqual(['2.9', '2.10']);
    expect(mergeVersionOptions([], [])).toEqual([]);
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
