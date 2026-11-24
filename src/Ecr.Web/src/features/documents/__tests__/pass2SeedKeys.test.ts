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

/*
 * ✎ B74 (людина 24.11, live): «у проєкту вже є документ… або натисніть Next, щоб створити ще один» — проєкт тримає
 * документи кількох шаблонів, а сервер на шаблоні «один документ на проєкт» відмовляє `409 ECR-DOC-4092`. Тексти
 * — про документ ЦЬОГО ШАБЛОНУ і без запрошення створити ще один; розгорнуті бази — рядком `#UiChanged` старе → нове.
 */
describe('B74: тексти «документ уже є» — про шаблон, без «Next — ще один»', () => {
  const old: Record<string, Record<'en' | 'ru' | 'kz', string>> = {
    'documents.createExistsTitle': {
      en: 'This project already has a document',
      ru: 'У этого проекта уже есть документ',
      kz: 'Бұл жобаның құжаты бұрыннан бар',
    },
    'documents.createExistsText': {
      en: '{key} is already kept for this project. One document per object, all months inside it: open it, or press Next to create another one.',
      ru: '{key} уже ведётся по этому проекту. Один документ — на объект, все месяцы внутри него: откройте его или нажмите «Далее», чтобы создать ещё один.',
      kz: '{key} осы жоба бойынша жүргізіліп жатыр. Бір нысанға бір құжат, барлық айлар оның ішінде: оны ашыңыз немесе тағы біреуін құру үшін «Келесі» басыңыз.',
    },
    'documents.createExistsReview': {
      en: 'The project already has {key}: another document will be created.',
      ru: 'У проекта уже есть {key}: будет создан ещё один документ.',
      kz: 'Жобада {key} бұрыннан бар: тағы бір құжат құрылады.',
    },
  };

  it('B74: en/ru/kz кажуть про шаблон і не запрошують «Next — створити ще один»', () => {
    for (const key of Object.keys(old)) {
      expect(catalogValue(key, 'en'), key).toMatch(/template/);
      expect(catalogValue(key, 'en'), key).not.toMatch(/press Next|create another/);
      expect(catalogValue(key, 'ru'), key).toMatch(/шаблон/);
      expect(catalogValue(key, 'ru'), key).not.toMatch(/«Далее»|создать ещё один/);
      expect(catalogValue(key, 'kz'), key).toMatch(/үлгі/);
      expect(catalogValue(key, 'kz'), key).not.toMatch(/«Келесі»|тағы біреуін құру/);
    }
  });

  it('B74: на кожен ключ і мову є рядок «змінені тексти» старе → нове', () => {
    const changed = seed.slice(seed.indexOf('INSERT INTO #UiChanged'));
    for (const [key, byLang] of Object.entries(old)) {
      for (const lang of ['en', 'ru', 'kz'] as const) {
        const current = (catalogValue(key, lang) ?? '').replace(/'/g, "''");
        const row = `(N'${key}', N'${lang}', N'${(byLang[lang] ?? '').replace(/'/g, "''")}', N'${current}')`;
        expect(changed.includes(row), `немає рядка змінених текстів ${key} / ${lang}`).toBe(true);
      }
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
