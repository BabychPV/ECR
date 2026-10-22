import { readFileSync, readdirSync, statSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * Ключі каталогу екрана «Groups» і цілей Lookup із групами: усе, що код просить у `t()` під `permits.groups.*` та
 * `registries.lookupTargetUnlisted`/`registries.lookupGroup.unlisted`, є в секції `PERMITS: groups-ui` сіду трьома мовами,
 * з однаковими підстановками; англійський текст без кирилиці; зайвих ключів у секції немає.
 */

const webRoot = process.cwd();
const groupsDir = path.resolve(webRoot, 'src', 'features', 'permits', 'groups');
const targetsFile = path.resolve(webRoot, 'src', 'features', 'registries', 'lookupTargets.ts');
const seed = readFileSync(path.resolve(webRoot, '..', 'Ecr.Infrastructure', 'Persistence', 'Sql', '09-seed.sql'), 'utf8');

const marker = {
  en: '-- PERMITS: groups-ui ── en:',
  rukz: '-- PERMITS: groups-ui ── ru/kz',
  end: '-- PERMITS: groups-ui ── кінець секції ──',
};

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const full = path.join(dir, name);
    if (statSync(full).isDirectory()) return name === '__tests__' ? [] : sources(full);

    return /\.tsx?$/.test(name) ? [full] : [];
  });
}

function usedKeys(): Set<string> {
  const keys = new Set<string>();
  for (const file of [...sources(groupsDir), targetsFile]) {
    const text = readFileSync(file, 'utf8');
    for (const match of text.matchAll(/permits\.groups\.[A-Za-z.]+[A-Za-z]/g)) keys.add(match[0]);
    for (const match of text.matchAll(/registries\.(?:lookupTargetUnlisted|lookupGroup\.unlisted)/g)) keys.add(match[0]);
  }

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

describe('каталог екрана Groups', () => {
  const en = section('en');
  const ru = section('ru');
  const kz = section('kz');
  const used = usedKeys();

  it('код просить ключі, і вони всі є в en, ru і kz', () => {
    expect(used.size).toBeGreaterThan(40);
    expect([...used].filter((key) => !en.has(key) || !ru.has(key) || !kz.has(key))).toEqual([]);
  });

  it('в секції немає зайвих ключів, а мови збігаються за складом', () => {
    expect([...en.keys()].filter((key) => !used.has(key))).toEqual([]);
    expect([...ru.keys()].sort()).toEqual([...en.keys()].sort());
    expect([...kz.keys()].sort()).toEqual([...en.keys()].sort());
  });

  it('жоден ключ секції не повторює ключ з іншого місця сіду (дубль у MERGE)', () => {
    const begin = seed.indexOf(marker.en);
    const outside = seed.slice(0, begin) + seed.slice(seed.indexOf(marker.end, begin));
    expect([...en.keys()].filter((key) => outside.includes(`N'${key}', N'en'`))).toEqual([]);
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
