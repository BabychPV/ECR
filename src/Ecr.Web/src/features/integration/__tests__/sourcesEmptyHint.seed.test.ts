import { describe, expect, it } from 'vitest';
import { readSeedEnCatalog, readSeedSql } from '@/test/seedSql';

/**
 * ✎ DOCUX (огляд UI 27.11): порожній перелік джерел (/admin/sources) показував шляхи файлів репозиторію («See
 * docs/admin/admin-guide.md, section 5…») і називав лише PI Web API. Тепер — без шляхів, усі види підключень (RC b17).
 * Остання порція сіду (епілог `#UiChanged` бере останнє значення) — у `DOCUX-ui-review.sql`.
 */
const seed = `${readSeedEnCatalog()}\n${readSeedSql()}`;

function values(lang: 'en' | 'ru' | 'kz'): string[] {
  return [...seed.matchAll(new RegExp(`\\(N'sources\\.notConfiguredHint', N'${lang}', N'((?:[^']|'')*)'`, 'g'))].map((match) => match[1] ?? '');
}

describe('sources.notConfiguredHint', () => {
  it.each(['en', 'ru', 'kz'] as const)('%s — без шляхів репозиторію, усі види підключень', (lang) => {
    const found = values(lang);
    expect(found.length).toBeGreaterThan(0);
    for (const text of found) {
      expect(text).not.toMatch(/docs\/|\.md\b/);
      expect(text).toContain('PI Web API');
      expect(text).toContain('PI SQL Client');
      expect(text).toMatch(/\bSQL\b.*\bSQL\b/);
    }
  });
});
