import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { readSeedSql } from '@/test/seedSql';

/**
 * Feature (j) P3 catalog (Ship 2: + `permits.header.*`): every `permits.cell.*` / `permits.history.*` / `permits.split.*` key the code asks for is in
 * the `PERMITS: midmonth-split-ui` section in en, ru and kz with the same placeholders; no extra keys; English without
 * Cyrillic, ru/kz translated; no key duplicated elsewhere in the seed.
 */
const webRoot = process.cwd();
const historyDir = path.resolve(webRoot, 'src', 'features', 'permits', 'history');
const others = [
  path.resolve(webRoot, 'src', 'features', 'grid', 'DocumentGrid.tsx'),
  path.resolve(webRoot, 'src', 'features', 'documents', 'inspector', 'DocumentInspector.tsx'),
];
const seed = readSeedSql();

const marker = {
  en: '-- PERMITS: midmonth-split-ui ── en:',
  rukz: '-- PERMITS: midmonth-split-ui ── ru/kz',
  end: '-- PERMITS: midmonth-split-ui ── кінець секції ──',
};

function usedKeys(): Set<string> {
  const files = [
    ...readdirSync(historyDir)
      .filter((name) => /\.tsx?$/.test(name))
      .map((name) => path.join(historyDir, name)),
    ...others,
  ];
  const keys = new Set<string>();
  for (const file of files) {
    for (const match of readFileSync(file, 'utf8').matchAll(/permits\.(?:cell|history|split|header)\.[A-Za-z]+/g)) keys.add(match[0]);
  }

  return keys;
}

function section(lang: 'en' | 'ru' | 'kz'): Map<string, string> {
  const rows = new Map<string, string>();
  const pattern = new RegExp(`\\(N'([^']+)', N'${lang}', N'((?:[^']|'')*)'`, 'g');
  const begin = seed.indexOf(lang === 'en' ? marker.en : marker.rukz);
  const end = seed.indexOf(marker.end, begin);
  expect(begin, `section ${lang}`).toBeGreaterThan(0);
  expect(end, `end of section ${lang}`).toBeGreaterThan(begin);

  for (const match of seed.slice(begin, end).matchAll(pattern)) {
    rows.set(match[1] as string, (match[2] as string).replace(/''/g, "'"));
  }

  return rows;
}

const placeholders = (text: string): string => [...text.matchAll(/\{(\w+)\}/g)].map((m) => m[1] as string).sort().join(',');

describe('catalog of feature (j) UI', () => {
  const en = section('en');
  const ru = section('ru');
  const kz = section('kz');
  const used = usedKeys();

  it('every key the code asks for is in en, ru and kz', () => {
    expect(used.size).toBeGreaterThan(40);
    expect([...used].filter((key) => !en.has(key) || !ru.has(key) || !kz.has(key))).toEqual([]);
  });

  it('no extra keys; the languages have the same keys', () => {
    expect([...en.keys()].filter((key) => !used.has(key))).toEqual([]);
    expect([...ru.keys()].sort()).toEqual([...en.keys()].sort());
    expect([...kz.keys()].sort()).toEqual([...en.keys()].sort());
  });

  it('no key of the section appears elsewhere in the seed', () => {
    const begin = seed.indexOf(marker.rukz);
    const outside = seed.slice(0, begin) + seed.slice(seed.lastIndexOf(marker.end) + marker.end.length);
    expect([...en.keys()].filter((key) => outside.includes(`N'${key}', N'`))).toEqual([]);
  });

  it('placeholders are the same in all three languages', () => {
    expect(
      [...en.entries()]
        .filter(([key, text]) => placeholders(ru.get(key) ?? '') !== placeholders(text) || placeholders(kz.get(key) ?? '') !== placeholders(text))
        .map(([key]) => key),
    ).toEqual([]);
  });

  it('English without Cyrillic; ru/kz translated', () => {
    expect([...en.entries()].filter(([, text]) => /[Ѐ-ӿ]/.test(text)).map(([key]) => key)).toEqual([]);
    for (const map of [ru, kz]) {
      expect([...map.entries()].filter(([, text]) => !/[Ѐ-ӿ]/.test(text)).map(([key]) => key)).toEqual([]);
    }
  });
});
