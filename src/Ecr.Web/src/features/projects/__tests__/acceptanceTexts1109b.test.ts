import { describe, expect, it } from 'vitest';
import { readSeedEnCatalog, readSeedSql } from '@/test/seedSql';

/**
 * Приймання 08.11 (лінія f/g), свіжі очі — тексти сіду en/ru/kz:
 * - g4: порожній відфільтрований перелік радив «turn off "Mine"», а такого перемикача немає — є поле «Owner» зі
 *   значенням «Everyone’s documents» (`documents.ownersAll`);
 * - a5: підказка на Jobs показувала сирий код права «System.ViewHealth» замість його назви (`permission.*`).
 *
 * ⚠ ru/kz — рядки з ТРЬОМА значеннями (`#I18N`): секція «Змінені тексти» має чотири (ключ, мова, старе, нове), і
 * регулярний вираз нижче її не бере.
 */
function en(key: string): string {
  const escaped = key.replace(/\./gu, '\\.');
  const row = new RegExp(`\\(N'${escaped}',\\s*N'en',\\s*N'([^']*)'`, 'u').exec(readSeedEnCatalog());
  expect(row, `сід: в en-каталозі немає ключа ${key}`).not.toBeNull();
  return row?.[1] ?? '';
}

function i18n(key: string, lang: 'ru' | 'kz'): string {
  const escaped = key.replace(/\./gu, '\\.');
  const row = new RegExp(`\\(N'${escaped}',\\s*N'${lang}',\\s*N'([^']*)'\\)`, 'u').exec(readSeedSql());
  expect(row, `сід: немає ключа ${key} (${lang})`).not.toBeNull();
  return row?.[1] ?? '';
}

describe('приймання 08.11 — тексти g4, a5', () => {
  it('g4: підказка називає поле «Owner» і його значення «Everyone’s documents», а не «Mine»', () => {
    expect(en('documents.noMatchHint')).toContain(en('documents.owner'));
    expect(en('documents.noMatchHint')).toContain(en('documents.ownersAll'));
    expect(en('documents.noMatchHint')).not.toMatch(/Mine/u);

    for (const lang of ['ru', 'kz'] as const) {
      const hint = i18n('documents.noMatchHint', lang);
      expect(hint, lang).toContain(i18n('documents.owner', lang));
      expect(hint, lang).toContain(i18n('documents.ownersAll', lang));
      expect(hint, lang).not.toContain(lang === 'ru' ? '«Мои»' : '«Менікі»');
    }
  });

  it('a5: підказка Jobs — назва права через {permission}, без сирого коду', () => {
    expect(en('jobs.mineOnlyHint')).not.toContain('System.ViewHealth');
    expect(en('jobs.mineOnlyHint')).toContain('{permission}');

    for (const lang of ['ru', 'kz'] as const) {
      expect(i18n('jobs.mineOnlyHint', lang), lang).not.toContain('System.ViewHealth');
      expect(i18n('jobs.mineOnlyHint', lang), lang).toContain('{permission}');
    }
  });
});
