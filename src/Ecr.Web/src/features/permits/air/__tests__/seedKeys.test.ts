import { readFileSync, readdirSync, statSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * Ключі каталогу картки лімітів і майстра імпорту (NAE-план N3/N5): усе, що код просить у `t()` під `permits.air.*`
 * (і вкладка `permits.page.tabs.air`), є в секціях `COLL:ui-permit-card` / `COLL:ui-air-import` сіду трьома мовами,
 * з однаковими підстановками; англійський текст без кирилиці; пар (ключ, мова) без дублів; зайвих ключів у секціях немає.
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
  ...sources(path.resolve(webRoot, 'src', 'features', 'permits', 'air')),
  path.resolve(webRoot, 'src', 'pages', 'admin', 'PermitAirImportPage.tsx'),
  path.resolve(webRoot, 'src', 'pages', 'admin', 'PermitPage.tsx'),
  path.resolve(webRoot, 'src', 'app', 'routes.ts'),
];

const used = new Set<string>();
for (const file of files) {
  for (const match of readFileSync(file, 'utf8').matchAll(/'(permits\.air(?:\.[A-Za-z0-9]+)*|permits\.page\.tabs\.air)'/g)) {
    used.add(match[1] ?? '');
  }
}

const sectionNames = ['ui-permit-card', 'ui-air-import'];

/** Рядки секцій за мовою: ключ -> тексти (кілька = дубль). */
function rows(lang: 'en' | 'ru' | 'kz'): Map<string, string[]> {
  const out = new Map<string, string[]>();
  const pattern = new RegExp(`\\(N'([^']+)', N'${lang}', N'((?:[^']|'')*)'`, 'g');

  for (const name of sectionNames) {
    const marks = [...seed.matchAll(new RegExp(`-- COLL:${name} ── (en|ru/kz):[^\\n]*\\n`, 'g'))];
    for (const mark of marks) {
      const isEn = mark[1] === 'en';
      if (isEn !== (lang === 'en')) continue;
      const begin = (mark.index ?? 0) + mark[0].length;
      const end = seed.indexOf(`-- COLL:${name} ── кінець секції ──`, begin);
      for (const row of seed.slice(begin, end).matchAll(pattern)) {
        const list = out.get(row[1] ?? '') ?? [];
        list.push((row[2] ?? '').replaceAll("''", "'"));
        out.set(row[1] ?? '', list);
      }
    }
  }

  return out;
}

const placeholders = (text: string): string => [...text.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort().join(',');

describe('ключі permits.air.* у сіді', () => {
  const en = rows('en');
  const ru = rows('ru');
  const kz = rows('kz');

  it('код знайшов ключі', () => {
    expect(used.size).toBeGreaterThan(100);
  });

  it('кожен ключ коду є в сіді трьома мовами', () => {
    for (const key of used) {
      expect(en.has(key), `en: ${key}`).toBe(true);
      expect(ru.has(key), `ru: ${key}`).toBe(true);
      expect(kz.has(key), `kz: ${key}`).toBe(true);
    }
  });

  it('зайвих ключів у секціях немає (код їх не просить)', () => {
    for (const key of [...en.keys(), ...ru.keys(), ...kz.keys()]) expect(used.has(key), `зайвий: ${key}`).toBe(true);
  });

  it('пар (ключ, мова) без дублів, підстановки однакові, англійська без кирилиці', () => {
    for (const [lang, map] of [['en', en], ['ru', ru], ['kz', kz]] as const) {
      for (const [key, texts] of map) expect(texts, `дубль ${lang}: ${key}`).toHaveLength(1);
    }
    for (const key of used) {
      const base = placeholders(en.get(key)?.[0] ?? '');
      expect(placeholders(ru.get(key)?.[0] ?? ''), `ru підстановки: ${key}`).toBe(base);
      expect(placeholders(kz.get(key)?.[0] ?? ''), `kz підстановки: ${key}`).toBe(base);
      expect(/[А-Яа-яЁё]/.test(en.get(key)?.[0] ?? ''), `кирилиця в en: ${key}`).toBe(false);
    }
  });

  it('ключі не зустрічаються в сіді поза своїми секціями', () => {
    const outside = seed
      .replace(/-- COLL:(ui-permit-card|ui-air-import) ── (?:en|ru\/kz):[\s\S]*?-- COLL:\1 ── кінець секції ──/g, '');
    expect(outside).not.toMatch(/N'permits\.air\./);
    expect(outside).not.toMatch(/N'permits\.page\.tabs\.air'/);
  });
});
