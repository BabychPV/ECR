import { readFileSync, readdirSync, statSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * Ключі каталогу діалогів і вкладок конструктора типу (PM-08c): усе, що код просить у `t()`,
 * є в секції `PERMITS: editor-dialogs` сіду трьома мовами, з однаковими підстановками; англійський
 * текст без кирилиці; зайвих ключів у секції немає.
 *
 * ⚠ Сід читається як текст: секція має власні маркери (`── PM-08c` … `кінець секції`), а ru/kz —
 * власну порцію нижче (копія розкладки `PERMITS: shell`/`types`).
 */

const root = path.resolve(process.cwd(), 'src', 'features', 'permits', 'typeEditor');
const seedPath = path.resolve(process.cwd(), '..', 'Ecr.Infrastructure', 'Persistence', 'Sql', '09-seed.sql');
const seed = readFileSync(seedPath, 'utf8');

/*
 * Каталоги PM-08c: діалоги, вкладки Checks і Versions. Сторінка редактора (PM-08b) має власну секцію
 * `PERMITS: editor`, а ключі зведення (видалення таблиці, відмова збереження) — секцію `ui-wiring`;
 * тест бачить обидві з тих, що належать цим каталогам.
 */
const ownDirs = ['dialogs', 'checks', 'versions'].map((name) => path.join(root, name));
const sectionMarkers = [
  { en: '-- PERMITS: editor-dialogs ── PM-08c', rukz: '-- PERMITS: editor-dialogs ── ru/kz', end: '-- PERMITS: editor-dialogs ── кінець секції ──' },
  { en: '-- PERMITS: ui-wiring ── зведення', rukz: '-- PERMITS: ui-wiring ── ru/kz', end: '-- PERMITS: ui-wiring ── кінець секції ──' },
  // PM-03b2: кількості в «Remove table» — власна секція в кінці блоку.
  { en: '-- PERMITS: type-usage ── en: PM-03b2', rukz: '-- PERMITS: type-usage ── ru/kz', end: '-- PERMITS: type-usage ── кінець секції ──' },
];

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const full = path.join(dir, name);
    if (statSync(full).isDirectory()) return name === '__tests__' ? [] : sources(full);

    return /\.tsx?$/.test(name) ? [full] : [];
  });
}

/** Ключі, яких просить код (усі — літералами: динамічні ключі забороняє `EndpointCoverageTests`). */
function usedKeys(): Set<string> {
  const keys = new Set<string>();
  for (const file of ownDirs.flatMap(sources)) {
    const text = readFileSync(file, 'utf8');
    for (const match of text.matchAll(/permits\.editor\.[A-Za-z.]+[A-Za-z]/g)) keys.add(match[0]);
  }

  return keys;
}

/** Рядки секції за мовами: ключ → текст. */
function section(lang: 'en' | 'ru' | 'kz'): Map<string, string> {
  const rows = new Map<string, string>();
  const pattern = new RegExp(`\\(N'([^']+)', N'${lang}', N'((?:[^']|'')*)'`, 'g');
  for (const marker of sectionMarkers) {
    const begin = seed.indexOf(lang === 'en' ? marker.en : marker.rukz);
    const end = seed.indexOf(marker.end, begin);
    expect(begin, `секція ${lang} (${marker.end}) є`).toBeGreaterThan(0);
    expect(end, `кінець секції ${lang} (${marker.end}) є`).toBeGreaterThan(begin);

    for (const match of seed.slice(begin, end).matchAll(pattern)) {
      rows.set(match[1] as string, (match[2] as string).replace(/''/g, "'"));
    }
  }

  return rows;
}

const placeholders = (text: string): string[] => [...text.matchAll(/\{(\w+)\}/g)].map((m) => m[1] as string).sort();

describe('каталог діалогів конструктора типу', () => {
  const en = section('en');
  const ru = section('ru');
  const kz = section('kz');
  const used = usedKeys();

  it('усі ключі коду є в en, ru і kz', () => {
    const missing = [...used].filter((key) => !en.has(key) || !ru.has(key) || !kz.has(key));
    expect(missing).toEqual([]);
  });

  it('в секції немає зайвих ключів і мови збігаються за складом', () => {
    expect([...en.keys()].filter((key) => !used.has(key))).toEqual([]);
    expect([...ru.keys()].sort()).toEqual([...en.keys()].sort());
    expect([...kz.keys()].sort()).toEqual([...en.keys()].sort());
  });

  it('підстановки {…} однакові в усіх трьох мовах', () => {
    const broken = [...en.entries()]
      .filter(([key, text]) => {
        const expected = placeholders(text).join(',');

        return placeholders(ru.get(key) ?? '').join(',') !== expected || placeholders(kz.get(key) ?? '').join(',') !== expected;
      })
      .map(([key]) => key);
    expect(broken).toEqual([]);
  });

  it('англійський текст без кирилиці, ru/kz не порожні й справді перекладені', () => {
    expect([...en.entries()].filter(([, text]) => /[Ѐ-ӿ]/.test(text)).map(([key]) => key)).toEqual([]);

    // PUI-7: приклад назви таблиці теж перекладений (раніше був виняток «латиницею»).
    for (const map of [ru, kz]) {
      const untranslated = [...map.entries()]
        .filter(([, text]) => text.trim().length === 0 || !/[Ѐ-ӿ]/.test(text))
        .map(([key]) => key);
      expect(untranslated).toEqual([]);
    }
  });
});
