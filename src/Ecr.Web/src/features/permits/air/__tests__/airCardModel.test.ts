import { describe, expect, it, vi } from 'vitest';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import {
  addedText,
  cell,
  changedValues,
  columnTitle,
  Dash,
  filterSources,
  num,
  scopeLabel,
  sourceTypeLabel,
  sourceTypeShort,
  type EditField,
} from '../airCardModel';
import { nodeOptions } from '../import/naeNodes';
import { card, tree } from './airFixtures';

vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string) => Catalog[key] ?? `⟦${key}⟧`,
}));

describe('airCardModel: додані колонки й правка (P1 07.11)', () => {
  const column = { table: 'sources', code: 'FILE_B', nameL10n: { en: 'Workshop', ru: 'Цех' }, dataType: 'Decimal' };

  it('заголовок — назва мовою, інакше англійська, інакше код; число без хвостових нулів, порожнє — прочерк', () => {
    expect(columnTitle(column, 'ru')).toBe('Цех');
    expect(columnTitle(column, 'kz')).toBe('Workshop');
    expect(columnTitle({ ...column, nameL10n: {} }, 'en')).toBe('FILE_B');
    expect(addedText({ FILE_B: '0.5000000000000000' }, column)).toBe('0.5');
    expect(addedText({}, column)).toBe(Dash);
    expect(addedText(null, column)).toBe(Dash);
  });

  it('у запис іде лише змінене: порожнє — null, число — числом, число з комою — текстом (сервер читає за мовою)', () => {
    const fields: EditField[] = [
      { code: 'A', label: 'A', kind: 'number', added: false },
      { code: 'B', label: 'B', kind: 'number', added: true },
      { code: 'C', label: 'C', kind: 'text', added: false },
      { code: 'D', label: 'D', kind: 'text', added: true },
    ];
    expect(changedValues(fields, { A: '1', B: '2', C: 'x', D: 'y' }, { A: '1', B: '2,5', C: '', D: ' y ' })).toEqual({ B: '2,5', C: null });
    expect(changedValues(fields, { A: '1', B: '', C: '', D: '' }, { A: '1.25', B: '', C: '', D: '' })).toEqual({ A: 1.25 });
  });
});

describe('airCardModel', () => {
  it('область рядка: коди записів Region/Area, інакше текст; «-» (без ділянки) не показується', () => {
    const base = card().sources[0]!;
    expect(scopeLabel({ ...base, region: 'ATYRAU', area: 'OFFSHORE', regionEntryCode: 'REG_001', areaEntryCode: 'AREA_018' })).toBe('REG_001 / AREA_018');
    expect(scopeLabel({ ...base, region: 'Atyrau', area: '-', regionEntryCode: null, areaEntryCode: null })).toBe('Atyrau');
    expect(scopeLabel({ ...base, region: null, area: null, regionEntryCode: null, areaEntryCode: null })).toBe(Dash);
  });

  it('тип джерела: латинські T/P1 з сервера -> підписи каталогу; невідомий показується як є', () => {
    expect(sourceTypeLabel('T')).toBe(Catalog['permits.air.type.point']);
    expect(sourceTypeLabel('P1')).toBe(Catalog['permits.air.type.diffuse']);
    expect(sourceTypeLabel('X9')).toBe('X9');
    expect(sourceTypeLabel(null)).toBe(Dash);
    expect(sourceTypeShort('P1')).toBe(Catalog['permits.air.type.diffuseShort']);
  });

  it('порожнє значення - прочерк, число рядком - як є (без округлення)', () => {
    expect(cell(null)).toBe(Dash);
    expect(cell('')).toBe(Dash);
    expect(cell('72.3171311')).toBe('72.3171311');
  });

  it('num: реальний сервер віддає повну шкалу (16 знаків) - нулі хвоста відкидаються, значення не округлюється', () => {
    expect(num('0.5000000000000000')).toBe('0.5');
    expect(num('25.0000000000000000')).toBe('25');
    expect(num('0.0000000000035000')).toBe('0.0000000000035');
    expect(num('-1.2500')).toBe('-1.25');
    expect(num('100')).toBe('100');
    expect(num('0.0000000000000001')).toBe('0.0000000000000001');
    expect(num(null)).toBe(Dash);
    expect(num('n/a')).toBe('n/a');
  });

  it('пошук: код НАЕ, назва джерела, код і назва речовини', () => {
    const { sources } = card();
    expect(filterSources(sources, '').length).toBe(2);
    expect(filterSources(sources, '1085').map((s) => s.naeCode)).toEqual(['1085']);
    expect(filterSources(sources, 'wellheads').map((s) => s.naeCode)).toEqual(['6001']);
    expect(filterSources(sources, '0333').map((s) => s.naeCode)).toEqual(['6001']);
    expect(filterSources(sources, 'sulphide').map((s) => s.naeCode)).toEqual(['6001']);
    expect(filterSources(sources, 'nothing')).toEqual([]);
  });
});

describe('nodeOptions', () => {
  it('вузли без джерел, підпис - шлях від кореня', () => {
    expect(nodeOptions(tree().nodes).map((o) => o.label)).toEqual(['Atyrau', 'Atyrau › Offshore', 'Atyrau › Offshore › DC05_Island']);
  });

  it('закритий вузол (validTo в минулому, isActive лишається true) не пропонується; чинний до дати - так', () => {
    const [first, second, third] = tree().nodes;
    const nodes = [
      { ...first!, validTo: '2026-01-31' },
      { ...second!, validTo: '2026-12-31' },
      { ...third!, validTo: null },
    ];
    expect(nodeOptions(nodes, '2026-10-30').map((o) => o.label)).toEqual(['Atyrau › Offshore', 'Atyrau › Offshore › DC05_Island']);
  });

  it('сирота й цикл не зациклюють', () => {
    const nodes = [
      { ...tree().nodes[0]!, id: 10, parentEntryId: 11, display: 'A' },
      { ...tree().nodes[0]!, id: 11, parentEntryId: 10, display: 'B' },
    ];
    expect(nodeOptions(nodes)).toHaveLength(2);
  });
});
