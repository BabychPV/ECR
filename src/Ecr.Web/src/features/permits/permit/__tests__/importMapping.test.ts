import { describe, expect, it } from 'vitest';
import type { PermitTypeColumn } from '../../api/types';
import {
  autoMapping,
  looksLikeHeader,
  mappedLines,
  mappingProblems,
  mappingToSave,
  normalizeHeader,
  orderMapping,
} from '../importMapping';
import { lookupRequest, serverResolver } from '../importLookups';
import { planRows, unresolvedGroups } from '../importRows';
import { fieldsOf } from '../model';
import { column } from './permitFixtures';

/*
 * Зіставлення стовпців вставки/імпорту (людина 06.11) і розв'язання посилань сервером.
 *
 * Мутаційні докази (виконано вручну, кожну мутацію відновлено):
 * - `autoMapping`: без `taken` (стовпець таблиці двічі) валить «стовпець таблиці береться один раз»;
 * - `matchHeader`: збережене після назви (порядок перевірок) валить «збережене зіставлення важить більше за назву»;
 * - `mappingProblems`: без винятку `derivable` валить «Region/Area не обов'язкові, коли зіставлено НАЕ»;
 * - `planRows`: `form[code] !== ''` → `true` (виведення перезаписує файл) валить «виводяться лише порожні клітинки».
 */

const sources = {
  code: 'Sources',
  nameL10n: { en: 'Sources (NAE)' },
  order: 1,
  rules: [],
  columns: [
    column('NAE', { dataType: 'Lookup', listScope: 'SharedRegistry', listRegistryCode: 'NAE_TREE', required: true, key: true, nameL10n: { en: 'NAE', ru: 'НАЕ' }, order: 1 }),
    column('REGION', { dataType: 'Lookup', listScope: 'SharedRegistry', listRegistryCode: 'LAND_REGION', required: true, nameL10n: { en: 'Region', ru: 'Регион' }, order: 2 }),
    column('AREA', { dataType: 'Lookup', listScope: 'SharedRegistry', listRegistryCode: 'AIR_AREA', nameL10n: { en: 'Area', ru: 'Район' }, order: 3 }),
    column('SOURCE_NAME', { required: true, nameL10n: { en: 'Source name', ru: 'Наименование источника' }, order: 4 }),
    column('HEIGHT', { dataType: 'Decimal', nameL10n: { en: 'Height, m', ru: 'Высота, м' }, order: 5 }),
  ] satisfies PermitTypeColumn[],
};
const fields = fieldsOf(sources, new Map());
const noPicker = { sources: { shared: {}, permitTables: {} }, ids: {} };

describe('normalizeHeader', () => {
  it('регістр, пробіли, пунктуація, NFKC', () => {
    expect(normalizeHeader('  Height, m ')).toBe('heightm');
    expect(normalizeHeader('ВЫСОТА (м)')).toBe('высотам');
    expect(normalizeHeader('ＮＡＥ')).toBe('nae');
  });
});

describe('autoMapping', () => {
  it('за назвою будь-якою мовою і за кодом, у будь-якому порядку; невідомий заголовок - ignore', () => {
    expect(autoMapping(['Высота, м', 'source_name', 'Comment', 'нае'], fields)).toEqual(['HEIGHT', 'SOURCE_NAME', null, 'NAE']);
  });

  it('стовпець таблиці береться один раз - першим стовпцем файлу', () => {
    expect(autoMapping(['NAE', 'нае'], fields)).toEqual(['NAE', null]);
  });

  it('збережене зіставлення важить більше за назву', () => {
    expect(autoMapping(['Region', 'Code'], fields, { region: 'AREA', code: 'NAE' })).toEqual(['AREA', 'NAE']);
  });

  it('збережене на стовпець, якого в таблиці вже немає, не застосовується', () => {
    expect(autoMapping(['Gone'], fields, { gone: 'OLD' })).toEqual([null]);
  });
});

describe('orderMapping / looksLikeHeader', () => {
  it('без заголовка - за порядком, зайві праворуч - ignore', () => {
    expect(orderMapping(7, fields)).toEqual(['NAE', 'REGION', 'AREA', 'SOURCE_NAME', 'HEIGHT', null, null]);
  });

  it('заголовок упізнається, дані - ні', () => {
    expect(looksLikeHeader(['NAE', 'Source name', 'Something'], fields)).toBe(true);
    expect(looksLikeHeader(['0036', 'Boiler', '12,5'], fields)).toBe(false);
    expect(looksLikeHeader(['Stoff'], fields, { stoff: 'NAE' })).toBe(true);
    expect(looksLikeHeader([], fields)).toBe(false);
  });
});

describe('mappingProblems', () => {
  it('обов’язкові незіставлені й зіставлені двічі', () => {
    expect(mappingProblems(['SOURCE_NAME', 'SOURCE_NAME'], fields)).toEqual({ missingRequired: ['NAE', 'REGION'], duplicated: ['SOURCE_NAME'] });
  });

  it("Region/Area не обов'язкові, коли зіставлено НАЕ (виводяться з джерела)", () => {
    expect(mappingProblems(['NAE', 'SOURCE_NAME'], fields, ['REGION', 'AREA'], ['NAE']).missingRequired).toEqual([]);
    expect(mappingProblems(['SOURCE_NAME'], fields, ['REGION', 'AREA'], ['NAE']).missingRequired).toEqual(['NAE', 'REGION']);
  });
});

describe('mappedLines / mappingToSave', () => {
  it('клітинки в порядку стовпців таблиці; незіставлений - порожній', () => {
    expect(mappedLines([['12', 'Boiler', 'x', 'N1']], ['HEIGHT', 'SOURCE_NAME', null, 'NAE'], fields)).toEqual([['N1', '', '', 'Boiler', '12']]);
  });

  it('зберігається лише зіставлене, ключ - нормалізований заголовок', () => {
    expect(mappingToSave(['Height, m', 'Note', ''], ['HEIGHT', null, 'NAE'])).toEqual({ heightm: 'HEIGHT' });
  });
});

describe('серверне розв’язання і Region/Area з джерела НАЕ', () => {
  const mapping = { registryCode: 'PRM_AIR_Sources', columns: {}, rowVersion: null, updatedAt: null, naeColumns: ['NAE'], derivable: ['REGION', 'AREA'] };
  const result = {
    columns: [
      {
        column: 'NAE',
        registryCode: 'NAE_TREE',
        values: [
          { text: '0036', entryId: 501, label: 'Boiler', reason: null, matches: 1 },
          { text: 'Heater', entryId: 502, label: 'Heater', reason: null, matches: 1 },
          { text: 'Lone', entryId: 503, label: 'Lone', reason: null, matches: 1 },
          { text: 'Site 1', entryId: null, label: null, reason: 'notSource', matches: 0 },
        ],
      },
      { column: 'REGION', registryCode: 'LAND_REGION', values: [{ text: 'ATY', entryId: 71, label: 'Atyrau', reason: null, matches: 1 }] },
    ],
    derived: [
      { naeEntryId: 501, cells: [{ column: 'REGION', entryId: 71, label: 'Atyrau', reason: null, scope: 'ATYRAU' }, { column: 'AREA', entryId: 81, label: 'Offshore', reason: null, scope: 'OFFSHORE' }] },
      { naeEntryId: 502, cells: [{ column: 'REGION', entryId: 72, label: 'Mangystau', reason: null, scope: 'MANG' }, { column: 'AREA', entryId: null, label: null, reason: 'ambiguous', scope: 'X' }] },
      { naeEntryId: 503, cells: [{ column: 'REGION', entryId: null, label: null, reason: 'noScope', scope: null }] },
    ],
  };
  const resolver = serverResolver(fields, result, mapping, noPicker);

  it('запит - різні непорожні значення серверних стовпців', () => {
    expect(lookupRequest([['0036', '', '', 'a'], ['0036', 'ATY', '', 'b'], ['Heater', ' ', '', 'c']], fields)).toEqual([
      { column: 'NAE', values: ['0036', 'Heater'] },
      { column: 'REGION', values: ['ATY'] },
    ]);
  });

  it('виводяться лише порожні клітинки; кожна позначена; необов’язкова невиведена - не блокує', () => {
    const plan = planRows(
      [
        ['0036', '', '', 'Boiler stack', '12'],
        ['Heater', 'ATY', '', 'Heater stack', ''],
      ],
      fields,
      resolver,
      5,
    );
    expect(plan.issues).toEqual([]);
    expect(plan.rows.map((row) => row.item.values)).toEqual([
      { PERMIT: '5', NAE: '501', REGION: '71', AREA: '81', SOURCE_NAME: 'Boiler stack', HEIGHT: '12' },
      { PERMIT: '5', NAE: '502', REGION: '71', SOURCE_NAME: 'Heater stack' },
    ]);
    expect(plan.derived).toEqual([
      { row: 1, column: 'REGION', label: 'Atyrau' },
      { row: 1, column: 'AREA', label: 'Offshore' },
    ]);
    expect(plan.underived).toEqual([{ row: 2, column: 'AREA', reason: 'ambiguous' }]);
  });

  it("обов'язковий Region не виведено - зауваження notDerived, рядок не чистий; не джерело - notSource", () => {
    const plan = planRows([['Lone', '', '', 'x', ''], ['Site 1', '', '', 'y', '']], fields, resolver, 5);
    expect(plan.rows.map((row) => row.clean)).toEqual([false, false]);
    expect(unresolvedGroups(plan.issues)).toEqual([
      { column: 'REGION', text: 'noScope', reason: 'notDerived', rows: 1 },
      { column: 'NAE', text: 'Site 1', reason: 'notSource', rows: 1 },
    ]);
  });

  it('відповідь сервера ще не прийшла - план «у перевірці», не чистий і без зауважень', () => {
    const plan = planRows([['0036', '', '', 'x', '']], fields, serverResolver(fields, undefined, mapping, noPicker), 5);
    expect(plan.pending).toBe(true);
    expect(plan.issues).toEqual([]);
    expect(plan.rows[0]?.clean).toBe(false);
  });
});
