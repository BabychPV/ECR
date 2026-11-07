import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { readSeedSql } from '@/test/seedSql';

/**
 * Pass2 (людський прохід 07.11, P3): тексти пп. 4–7 у сіді трьома мовами.
 *
 *  - п. 7: підзаголовок переліку документів (`nav.documents.description`, маршрут `/`) казав «for one period» під
 *    «One document per object; all months are inside it» і вибором Year/Month — тобто суперечив перегляду року.
 *    Текст наявного ключа змінено, тож у порції `#UiChanged` є рядок (ключ, мова, старе, нове) на кожну мову —
 *    інакше розгорнуті бази лишилися б зі старим текстом (CLAUDE.md, «змінюєш текст наявного ключа в сіді»).
 *  - пп. 4–6: нові ключі, які просить код, є в en/ru/kz з однаковими підстановками.
 */

const seed = readSeedSql();
const webRoot = process.cwd();

/** Значення ключа мовою з каталогу (en — `#UiEn` зі Scope, ru/kz — `#I18N`). Останнє входження виграє, як у MERGE. */
function catalogValue(key: string, lang: 'en' | 'ru' | 'kz'): string | undefined {
  const escaped = key.replace(/\./g, '\\.');
  const pattern =
    lang === 'en'
      ? new RegExp(`\\(N'${escaped}', N'en', N'((?:[^']|'')*)', [01]\\)`, 'g')
      : new RegExp(`\\(N'${escaped}', N'${lang}', N'((?:[^']|'')*)'\\)`, 'g');
  let value: string | undefined;
  for (const match of seed.matchAll(pattern)) value = (match[1] as string).replace(/''/g, "'");

  return value;
}

const placeholders = (text: string): string => [...text.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort().join(',');

describe('Pass2 п. 7: підзаголовок переліку документів — про рік, а не про «один період»', () => {
  const old = {
    en: 'Reports of your projects for one period. Open a document to work in it.',
    ru: 'Отчёты ваших проектов за один период. Откройте документ, чтобы работать с ним.',
    kz: 'Бір кезеңдегі жобаларыңыздың есептері. Онымен жұмыс істеу үшін құжатты ашыңыз.',
  } as const;

  it('Pass2: en/ru/kz не кажуть «один період», а кажуть про обраний рік', () => {
    expect(catalogValue('nav.documents.description', 'en')).toMatch(/selected year/);
    expect(catalogValue('nav.documents.description', 'en')).not.toMatch(/one period/);
    expect(catalogValue('nav.documents.description', 'ru')).toMatch(/выбранный год/);
    expect(catalogValue('nav.documents.description', 'ru')).not.toMatch(/один период/);
    expect(catalogValue('nav.documents.description', 'kz')).toMatch(/жыл/);
    expect(catalogValue('nav.documents.description', 'kz')).not.toMatch(/кезеңдегі/);
  });

  it('Pass2: на кожну мову є рядок «змінені тексти» старе → нове (розгорнуті бази)', () => {
    for (const lang of ['en', 'ru', 'kz'] as const) {
      const current = catalogValue('nav.documents.description', lang) ?? '';
      const row = `(N'nav.documents.description', N'${lang}', N'${old[lang]}', N'${current.replace(/'/g, "''")}')`;
      const changed = seed.slice(seed.indexOf('INSERT INTO #UiChanged'));
      expect(changed.includes(row), `немає рядка змінених текстів для ${lang}`).toBe(true);
    }
  });
});

describe('Pass2 пп. 4–6: нові ключі каталогу', () => {
  const files = [
    path.resolve(webRoot, 'src', 'features', 'documents', 'CreateDocumentModal.tsx'),
    path.resolve(webRoot, 'src', 'features', 'projects', 'PeriodsOverview.tsx'),
  ];
  const keys = [
    'documents.createExistsTitle',
    'documents.createExistsText',
    'documents.createExistsOpen',
    'documents.createExistsShowAll',
    'documents.createExistsReview',
    'documents.createDiscardTitle',
    'documents.createDiscardText',
    'documents.createDiscardVerb',
    'documents.createKeepEditing',
    'periods.tile.notOpenDraft',
    'periods.tile.notOpenInactive',
    'periods.overview.statsScope',
  ];

  it('Pass2: код просить кожен ключ літералом', () => {
    const source = files.map((file) => readFileSync(file, 'utf8')).join('\n');
    expect(keys.filter((key) => !source.includes(`t('${key}'`))).toEqual([]);
  });

  it('Pass2: кожен ключ є в en, ru і kz; підстановки однакові; en без кирилиці', () => {
    const problems: string[] = [];
    for (const key of keys) {
      const en = catalogValue(key, 'en');
      const ru = catalogValue(key, 'ru');
      const kz = catalogValue(key, 'kz');
      if (en === undefined || ru === undefined || kz === undefined) {
        problems.push(`${key}: немає мови`);
        continue;
      }
      if (placeholders(ru) !== placeholders(en) || placeholders(kz) !== placeholders(en)) problems.push(`${key}: підстановки`);
      if (/[Ѐ-ӿ]/.test(en)) problems.push(`${key}: кирилиця в en`);
    }
    expect(problems).toEqual([]);
  });
});
