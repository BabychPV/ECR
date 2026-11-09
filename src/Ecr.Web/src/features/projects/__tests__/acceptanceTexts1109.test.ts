import { describe, expect, it } from 'vitest';
import { readSeedEnCatalog } from '@/test/seedSql';

/**
 * Приймання 08.11 (лінія f/g + A), тексти P3: ключі в тестах компонентів — не текст, тому самі речення
 * перевіряються по en-каталогу сіду (порції `#UiEn`, як `saveRefusalTexts.test.ts`).
 *
 * ⛔ Кожне твердження — рівно та вада, яку бачила людина:
 * - e1: тост політики обіцяв зміну «on their next calendar rebuild», хоча стани оновлюються одразу (PeriodStateCatchUp);
 * - a1: тост перерахунку на вкладці Periods посилав «above the list», а списку там немає; і «Queued N documents» з N;
 * - g1: легенда смуги казала «current month», а рамка — поточний ПЕРІОД проєкту (D-77), як у шапці.
 */
function value(key: string): string {
  const escaped = key.replace(/\./gu, '\\.');
  const row = new RegExp(`\\(N'${escaped}',\\s*N'en',\\s*N'([^']*)'`, 'u').exec(readSeedEnCatalog());

  expect(row, `сід: в en-каталозі немає ключа ${key}`).not.toBeNull();

  return row?.[1] ?? '';
}

describe('приймання 08.11 — тексти лінії f/g', () => {
  it('e1: збережена політика — стани місяців уже оновлено, без «next calendar rebuild»', () => {
    const text = value('periods.policyUpdated');

    expect(text.toLowerCase()).not.toContain('rebuild');
    expect(text).toBe('Policy saved. Month states updated.');
  });

  it('a1: тост перерахунку — «Queued N documents», прогрес «at the top of this page», не «above the list»', () => {
    for (const key of ['recalcProgress.queuedToast', 'recalcProgress.queuedDocumentsToast']) {
      expect(value(key), key).not.toContain('above the list');
      expect(value(key), key).toContain('at the top of this page');
    }

    expect(value('recalcProgress.queuedDocumentsToast')).toMatch(/^Queued \{documentCount\} for recalculation/u);
  });

  it('g1: легенда рамки — поточний період, а не календарний місяць', () => {
    expect(value('documents.strip.current')).toBe('Ringed box = current period');
  });
});
