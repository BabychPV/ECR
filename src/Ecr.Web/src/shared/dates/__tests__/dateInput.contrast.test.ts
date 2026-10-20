import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { AA, contrast, flatten } from '@/shared/theme/contrast';
import { surfaces, themeSurface } from '@/shared/theme/theme';

/**
 * P2-N1 (прохід B): колір днів-вихідних календаря. axe `color-contrast` у jsdom не рахується (немає розкладки й
 * застосованих стилів із CSS), тому порогу 4.5:1 доводимо ОБЧИСЛЕННЯМ над значеннями токенів з `dateInput.css` -
 * тими самими, що потрапляють у браузер, - проти тла поповера, hover і вибраного рядка в кожній темі.
 *
 * Мутаційний доказ (вручну, відновлено): повернути `#fa5252` (Mantine red-6) у `--ecr-weekend-day` - світла 3.28 і
 * темна 4.13 < 4.5, обидва тести червоні.
 *
 * ⚠ Що це НЕ доводить: справжній `axe` у браузері (анімація поповера, фактичне тло) - повторити на стенді.
 */
const css = readFileSync(resolve(__dirname, '../dateInput.css'), 'utf8');

function token(scheme: 'light' | 'dark'): string {
  const rule =
    scheme === 'light'
      ? /:root\s*\{\s*--ecr-weekend-day:\s*(#[0-9a-f]{6})\s*;/i
      : /:root\[data-mantine-color-scheme='dark'\]\s*\{\s*--ecr-weekend-day:\s*(#[0-9a-f]{6})\s*;/i;
  const match = rule.exec(css);
  if (match?.[1] === undefined) throw new Error(`токен вихідних для теми ${scheme} не знайдено у dateInput.css`);

  return match[1];
}

// Тло, на якому лежить день: поповер (axe бачив #fff / #2e2e2e), поверхні теми, hover і вибір рядка.
const Backdrops = {
  light: ['#ffffff', surfaces.light.ground, surfaces.light.raised, surfaces.light.hover, surfaces.light.select, surfaces.light.accentSoft],
  dark: ['#2e2e2e', themeSurface.dark.body, surfaces.dark.ground, surfaces.dark.surface, surfaces.dark.raised, surfaces.dark.hover, surfaces.dark.select, surfaces.dark.accentSoft],
} as const;

describe('календар: колір днів-вихідних', () => {
  for (const scheme of ['light', 'dark'] as const) {
    it(`${scheme}: вихідні ≥ ${String(AA.text)}:1 на всіх тлах`, () => {
      const color = token(scheme);
      for (const backdrop of Backdrops[scheme]) {
        expect(contrast(color, backdrop), `${color} на ${backdrop}`).toBeGreaterThanOrEqual(AA.text);
      }
    });

    it(`${scheme}: дні сусідніх місяців (70 % тексту над тлом) ≥ ${String(AA.text)}:1`, () => {
      const text = scheme === 'light' ? themeSurface.light.text : surfaces.dark.text;
      for (const backdrop of Backdrops[scheme]) {
        // color-mix(in srgb, text 70%, body): текст з непрозорістю 0.7 над тлом.
        const mixed = flatten(`rgba(${[1, 3, 5].map((i) => parseInt(text.slice(i, i + 2), 16)).join(',')},0.7)`, backdrop);
        expect(contrast(mixed, backdrop), `${mixed} на ${backdrop}`).toBeGreaterThanOrEqual(AA.text);
      }
    });
  }
});
