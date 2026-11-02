import { readFileSync, readdirSync, statSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * Ключі каталогу «Used in» (PERMIT-USAGE): усе, що код просить у `t()` під `permits.usage.*` (і вкладка
 * `permits.page.tabs.usage`), є в секції `COLL:ui-permit-usage` сіду трьома мовами, з однаковими підстановками;
 * англійський текст без кирилиці; пар (ключ, мова) без дублів у всьому сіді; зайвих ключів у секції немає.
 *
 * ⚠ Сід читається як текст: en-порція має маркер `-- COLL:ui-permit-usage ── en:`, ru/kz — `── ru/kz:`, обидві
 * закінчуються `-- COLL:ui-permit-usage ── кінець секції ──`.
 */
const webRoot = process.cwd();
const seed = readFileSync(path.resolve(webRoot, '..', 'Ecr.Infrastructure', 'Persistence', 'Sql', '09-seed.sql'), 'utf8');

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const full = path.join(dir, name);
    if (statSync(full).isDirectory()) return name === '__tests__' ? [] : sources(full);

    return /\.tsx?$/.test(name) ? [full] : [];
  });
}

const files = [
  ...sources(path.resolve(webRoot, 'src', 'features', 'permits', 'usage')),
  path.resolve(webRoot, 'src', 'features', 'permits', 'list', 'PermitsListColumns.tsx'),
  path.resolve(webRoot, 'src', 'pages', 'admin', 'PermitPage.tsx'),
];

const used = new Set<string>();
for (const file of files) {
  for (const match of readFileSync(file, 'utf8').matchAll(/'(permits\.usage(?:\.[A-Za-z0-9]+)+|permits\.page\.tabs\.usage)'/g)) {
    used.add(match[1] ?? '');
  }
}

/** Рядки секції за мовою: ключ -> тексти (кілька = дубль). */
function rows(lang: 'en' | 'ru' | 'kz'): Map<string, string[]> {
  const out = new Map<string, string[]>();
  const pattern = new RegExp(`\\(N'([^']+)', N'${lang}', N'((?:[^']|'')*)'`, 'g');
  const marker = lang === 'en' ? '-- COLL:ui-permit-usage ── en:' : '-- COLL:ui-permit-usage ── ru/kz:';
  const begin = seed.indexOf(marker);
  const end = seed.indexOf('-- COLL:ui-permit-usage ── кінець секції ──', begin);
  expect(begin, `секція ${lang} є`).toBeGreaterThan(0);
  expect(end, `кінець секції ${lang} є`).toBeGreaterThan(begin);

  for (const match of seed.slice(begin, end).matchAll(pattern)) {
    const key = match[1] as string;
    out.set(key, [...(out.get(key) ?? []), (match[2] as string).replace(/''/g, "'")]);
  }

  return out;
}

const placeholders = (text: string): string => [...text.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort().join(',');

describe('каталог «Used in» дозволу', () => {
  const en = rows('en');
  const ru = rows('ru');
  const kz = rows('kz');

  it('код просить ключі, і вони всі є в en, ru і kz', () => {
    expect(used.size).toBeGreaterThan(15);
    expect([...used].filter((key) => !en.has(key) || !ru.has(key) || !kz.has(key))).toEqual([]);
  });

  it('у секції немає зайвих ключів, а мови збігаються за складом', () => {
    expect([...en.keys()].filter((key) => !used.has(key))).toEqual([]);
    expect([...ru.keys()].sort()).toEqual([...en.keys()].sort());
    expect([...kz.keys()].sort()).toEqual([...en.keys()].sort());
  });

  it('пар (ключ, мова) без дублів — ні в секції, ні в решті сіду', () => {
    for (const map of [en, ru, kz]) {
      expect([...map.entries()].filter(([, texts]) => texts.length > 1).map(([key]) => key)).toEqual([]);
    }
    for (const key of en.keys()) {
      for (const lang of ['en', 'ru', 'kz']) {
        expect(seed.split(`(N'${key}', N'${lang}'`).length - 1, `${key}/${lang}`).toBe(1);
      }
    }
  });

  it('підстановки {…} однакові в усіх трьох мовах', () => {
    const broken = [...en.entries()]
      .filter(([key, [text]]) => {
        const expected = placeholders(text ?? '');

        return placeholders(ru.get(key)?.[0] ?? '') !== expected || placeholders(kz.get(key)?.[0] ?? '') !== expected;
      })
      .map(([key]) => key);
    expect(broken).toEqual([]);
  });

  it('англійський текст без кирилиці, ru/kz перекладені', () => {
    expect([...en.entries()].filter(([, [text]]) => /[Ѐ-ӿ]/.test(text ?? '')).map(([key]) => key)).toEqual([]);
    for (const map of [ru, kz]) {
      expect([...map.entries()].filter(([, [text]]) => !/[Ѐ-ӿ]/.test(text ?? '')).map(([key]) => key)).toEqual([]);
    }
  });
});
