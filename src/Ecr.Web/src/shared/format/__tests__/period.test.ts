import { readFileSync } from 'node:fs';
import path from 'node:path';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { loadCatalog, resetMissingReports, setLanguage } from '@/shared/i18n';
import { formatMonthYear, formatPeriodKey } from '@/shared/format';

/**
 * `A2-10`: підпис місяця періоду — з каталогу рядків, а не з `Intl`.
 *
 * ⛔ У Chrome/Edge немає ICU-даних казахської: `Intl.DateTimeFormat('kk')`
 * віддає «September 2026». Node має повну ICU, тож саме тут цього не видно —
 * тому казахський тест ще й ЗАБОРОНЯЄ звертання до `Intl.DateTimeFormat`:
 * результат не має залежати від того, яку ICU має браузер.
 *
 * ⚠ Рядки — СПРАВЖНІ, з `09-seed.sql`: інакше тест перевіряв би власну копію
 * перекладу, а не те, що отримає замовник.
 */

const Seed = readFileSync(
  path.resolve(process.cwd(), '../../src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql'),
  'utf8',
);

/** Усі рядки сіду мовою `lang` з ключем, що починається на `periods.month`. */
function seedStrings(lang: string): Record<string, string> {
  const strings: Record<string, string> = {};
  const row = /\(N'(periods\.month[\w.]*)',\s*N'(\w+)',\s*N'([^']*)'/g;

  for (const match of Seed.matchAll(row)) {
    if (match[2] === lang) strings[match[1]!] = match[3]!;
  }

  return strings;
}

function catalog(languageCode: string, strings: Record<string, string>): Response {
  return new Response(JSON.stringify({ languageCode, revision: 1, strings }), {
    status: 200,
    headers: { 'Content-Type': 'application/json', ETag: `"public-${languageCode}-1"` },
  });
}

const Languages = ['en', 'ru', 'kz'] as const;

beforeAll(async () => {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL) => {
      const url = String(input);

      for (const lang of Languages) {
        if (url.includes(`/ui-strings/${lang}`)) return Promise.resolve(catalog(lang, seedStrings(lang)));
      }

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );

  for (const lang of Languages) await loadCatalog(lang, 'public');

  vi.unstubAllGlobals();
});

afterEach(() => {
  vi.restoreAllMocks();
  resetMissingReports();
});

afterAll(() => {
  setLanguage('en');
  localStorage.clear();
});

describe('formatMonthYear: місяць мовою інтерфейсу з каталогу', () => {
  it('сід має місяць і формат для кожної мови продукту', () => {
    for (const lang of Languages) {
      expect(Object.keys(seedStrings(lang))).toHaveLength(13);
    }
  });

  it('en — як і раніше, «September 2026»', () => {
    setLanguage('en');

    expect(formatMonthYear(2026, 9)).toBe('September 2026');
    expect(formatMonthYear(2026, 1)).toBe('January 2026');
    expect(formatMonthYear(2026, 12)).toBe('December 2026');
  });

  it('ru — «Сентябрь 2026», без суфікса «г.» і з великої літери', () => {
    setLanguage('ru');

    expect(formatMonthYear(2026, 9)).toBe('Сентябрь 2026');
    expect(formatMonthYear(2026, 5)).toBe('Май 2026');
  });

  it('kz — казахською, і без звертання до Intl (у Chrome немає ICU kk)', () => {
    setLanguage('kz');
    const intl = vi.spyOn(Intl, 'DateTimeFormat');

    expect(formatMonthYear(2026, 9)).toBe('Қыркүйек 2026');

    const all = Array.from({ length: 12 }, (_, i) => formatMonthYear(2026, i + 1));
    expect(new Set(all).size).toBe(12);
    for (const caption of all) expect(caption).not.toMatch(/[A-Za-z]/);

    expect(intl).not.toHaveBeenCalled();
  });

  it('місяць поза 1…12 — порожньо, а не «Invalid Date» чи виняток', () => {
    setLanguage('en');

    expect(formatMonthYear(2026, 0)).toBe('');
    expect(formatMonthYear(2026, 13)).toBe('');
    expect(formatMonthYear(2026, 1.5)).toBe('');
  });
});

describe('formatPeriodKey: людська назва за periodKey мовою інтерфейсу', () => {
  it('місяць: en / ru / kz, а не технічний ключ 202610', () => {
    setLanguage('en');
    expect(formatPeriodKey(202610)).toBe('October 2026');
    setLanguage('ru');
    expect(formatPeriodKey(202610)).toBe('Октябрь 2026');
    setLanguage('kz');
    expect(formatPeriodKey(202610)).toBe('Қазан 2026');
  });

  it('невалідний ключ — порожньо', () => {
    setLanguage('en');

    expect(formatPeriodKey(202613)).toBe('');
    expect(formatPeriodKey(202600)).toBe('');
    expect(formatPeriodKey(Number.NaN)).toBe('');
  });

  it('рік за Yearly: лише послідовність 1; квартал за Quarterly: 1…4', () => {
    setLanguage('en');

    expect(formatPeriodKey(202601, 'Yearly')).toBe('2026');
    expect(formatPeriodKey(202605, 'Yearly')).toBe('');
    expect(formatPeriodKey(202605, 'Quarterly')).toBe('');
  });
});

describe('formatMonthYear: каталогу немає — відкат на Intl, не позначка і не виняток', () => {
  it('мова без каталогу й без ICU-даних не падає', () => {
    // `xx` — синтаксично правильний тег, якого ICU не знає: рівно випадок
    // `kk` у Chrome. Каталогу `xx` немає, але `t()` спершу відкочується на en.
    setLanguage('xx');

    expect(() => formatMonthYear(2026, 9)).not.toThrow();
    expect(formatMonthYear(2026, 9)).toBe('September 2026');
  });
});
