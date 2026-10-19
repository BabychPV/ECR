import { readFileSync, readdirSync, statSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * Ключі каталогу переліку дозволів (PM-07): усе, що код просить у `t()` під `permits.list.*`, є в секції
 * `PERMITS: ui-list` сіду трьома мовами, з однаковими підстановками; англійський текст без кирилиці; зайвих
 * ключів у секції немає.
 *
 * ⚠ Сід читається як текст: en-порція має маркер `── PM-07`, ru/kz — `── ru/kz`, обидві закінчуються
 * `── кінець секції ──`.
 */

const webRoot = process.cwd();
const listDir = path.resolve(webRoot, 'src', 'features', 'permits', 'list');
const pageFile = path.resolve(webRoot, 'src', 'pages', 'admin', 'PermitsPage.tsx');
const seedPath = path.resolve(webRoot, '..', 'Ecr.Infrastructure', 'Persistence', 'Sql', '09-seed.sql');
const seed = readFileSync(seedPath, 'utf8');

const marker = {
  en: '-- PERMITS: ui-list ── PM-07',
  rukz: '-- PERMITS: ui-list ── ru/kz',
  end: '-- PERMITS: ui-list ── кінець секції ──',
};

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
  for (const file of [...sources(listDir), pageFile]) {
    for (const match of readFileSync(file, 'utf8').matchAll(/permits\.list\.[A-Za-z.]+[A-Za-z]/g)) keys.add(match[0]);
  }

  return keys;
}

/** Рядки секції за мовою: ключ → текст. */
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

describe('каталог переліку дозволів', () => {
  const en = section('en');
  const ru = section('ru');
  const kz = section('kz');
  const used = usedKeys();

  it('код просить ключі, і вони всі є в en, ru і kz', () => {
    expect(used.size).toBeGreaterThan(50);
    expect([...used].filter((key) => !en.has(key) || !ru.has(key) || !kz.has(key))).toEqual([]);
  });

  it('в секції немає зайвих ключів, а мови збігаються за складом', () => {
    expect([...en.keys()].filter((key) => !used.has(key))).toEqual([]);
    expect([...ru.keys()].sort()).toEqual([...en.keys()].sort());
    expect([...kz.keys()].sort()).toEqual([...en.keys()].sort());
  });

  it('жоден ключ секції не повторює ключ з іншого місця сіду (дубль у MERGE)', () => {
    const outside = seed.slice(0, seed.indexOf(marker.en)) + seed.slice(seed.indexOf(marker.end, seed.indexOf(marker.en)));
    const duplicates = [...en.keys()].filter((key) => outside.includes(`N'${key}', N'en'`));
    expect(duplicates).toEqual([]);
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

  it('англійський текст без кирилиці, ru/kz не порожні й перекладені', () => {
    expect([...en.entries()].filter(([, text]) => /[Ѐ-ӿ]/.test(text)).map(([key]) => key)).toEqual([]);
    for (const map of [ru, kz]) {
      expect([...map.entries()].filter(([, text]) => !/[Ѐ-ӿ]/.test(text)).map(([key]) => key)).toEqual([]);
    }
  });
});
