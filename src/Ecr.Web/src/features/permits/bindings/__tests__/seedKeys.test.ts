import { readFileSync, readdirSync, statSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * Каталог прив'язки аркуша до дозволу (PM-11): усе, що код просить у `t()` (`permits.sheetBinding.*` плюс
 * два ключі вкладки конструктора), є в секції `PERMITS: sheet-binding-ui` сіду трьома мовами з однаковими
 * підстановками; англійський текст без кирилиці; зайвих ключів у секції немає.
 *
 * ⚠ Сід читається як текст: en-порція й ru/kz-порція мають власні маркери (як у `PERMITS: editor-dialogs`).
 */

const root = path.resolve(process.cwd(), 'src', 'features', 'permits', 'bindings');
const workspace = path.resolve(process.cwd(), 'src', 'features', 'templates', 'ctor', 'TableWorkspace.tsx');
const seedPath = path.resolve(process.cwd(), '..', 'Ecr.Infrastructure', 'Persistence', 'Sql', '09-seed.sql');
const seed = readFileSync(seedPath, 'utf8');

const marker = {
  en: '-- PERMITS: sheet-binding-ui ── PM-11',
  rukz: '-- PERMITS: sheet-binding-ui ── ru/kz',
  end: '-- PERMITS: sheet-binding-ui ── кінець секції ──',
};

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const full = path.join(dir, name);
    if (statSync(full).isDirectory()) return name === '__tests__' ? [] : sources(full);

    return /\.tsx?$/.test(name) ? [full] : [];
  });
}

/** Ключі, яких просить код (усі — літералами в `t('…')`). */
function usedKeys(): Set<string> {
  const keys = new Set<string>();
  for (const file of sources(root)) {
    for (const match of readFileSync(file, 'utf8').matchAll(/permits\.sheetBinding\.[A-Za-z.]+[A-Za-z]/g)) keys.add(match[0]);
  }
  for (const match of readFileSync(workspace, 'utf8').matchAll(/ctor\.(?:tab\.permit|permitHint)/g)) keys.add(match[0]);

  return keys;
}

function section(lang: 'en' | 'ru' | 'kz'): Map<string, string> {
  const rows = new Map<string, string>();
  const pattern = new RegExp(`\\(N'([^']+)', N'${lang}', N'((?:[^']|'')*)'`, 'g');
  const begin = seed.indexOf(lang === 'en' ? marker.en : marker.rukz);
  const end = seed.indexOf(marker.end, begin);
  expect(begin, `секція ${lang} є`).toBeGreaterThan(0);
  expect(end, `кінець секції ${lang} є`).toBeGreaterThan(begin);

  for (const match of seed.slice(begin, end).matchAll(pattern)) {
    rows.set(match[1] as string, (match[2] as string).replace(/''/g, "'"));
  }

  return rows;
}

const placeholders = (text: string): string[] => [...text.matchAll(/\{(\w+)\}/g)].map((m) => m[1] as string).sort();

describe('каталог прив’язки аркуша до дозволу', () => {
  const en = section('en');
  const ru = section('ru');
  const kz = section('kz');
  const used = usedKeys();

  it('код просить непорожній набір ключів', () => {
    expect(used.size).toBeGreaterThan(100);
  });

  it('усі ключі коду є в en, ru і kz', () => {
    expect([...used].filter((key) => !en.has(key) || !ru.has(key) || !kz.has(key))).toEqual([]);
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

  it('англійський текст без кирилиці; ru/kz не порожні й справді перекладені', () => {
    expect([...en.entries()].filter(([, text]) => /[Ѐ-ӿ]/.test(text)).map(([key]) => key)).toEqual([]);

    for (const map of [ru, kz]) {
      const untranslated = [...map.entries()].filter(([, text]) => text.trim().length === 0 || !/[Ѐ-ӿ]/.test(text)).map(([key]) => key);
      expect(untranslated).toEqual([]);
    }
  });

  it('кожне попередження сервера, яке розпізнає клієнт, має текст (випадки моделі = ключі каталогу)', () => {
    const kinds = readFileSync(path.join(root, 'bindingText.ts'), 'utf8').match(/permits\.sheetBinding\.warning\.[A-Za-z]+/g) ?? [];
    expect(kinds.length).toBeGreaterThanOrEqual(11);
    expect(kinds.filter((key) => !en.has(key))).toEqual([]);
  });
});
