import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { AA, contrast } from '../contrast';
import { brand } from '../theme';

/**
 * T2-05: рамка поля у фокусі в темній темі була `brand[5]` (#5558c8) на поверхні Paper/меню `#2e2e2e` —
 * 2.34:1 (< 3:1, WCAG 1.4.11). Поля Mantine гасять `outline` і міняють лише колір рамки, тож кільце з
 * `motion.css` на них не діє; правило `--input-bd: var(--ecr-focus)` дає їм `brand[4]`.
 */

/** `--mantine-color-dark-6` — заливка Paper, меню й самого поля в темній темі Mantine. */
const DarkPaper = '#2e2e2e';

const css = readFileSync(path.resolve(process.cwd(), 'src/shared/theme/motion.css'), 'utf8').replace(
  /\/\*[\s\S]*?\*\//g,
  '',
);

describe('T2-05: рамка поля у фокусі, темна тема', () => {
  it('першопричина виміряна: колір рамки за замовчуванням — нижче 3:1, колір кільця — вище', () => {
    // brand[5] — `--mantine-primary-color-filled` у темній схемі (primaryShade.dark = 5).
    expect(contrast(brand[5] ?? '', DarkPaper)).toBeLessThan(AA.nonText);
    // brand[4] — `--ecr-focus` у темній схемі.
    expect(contrast(brand[4] ?? '', DarkPaper)).toBeGreaterThanOrEqual(AA.nonText);
  });

  it('поле у фокусі бере --ecr-focus для рамки', () => {
    expect(css).toMatch(
      /:root\[data-mantine-color-scheme='dark'\]\s+\.mantine-Input-input:focus,\s*:root\[data-mantine-color-scheme='dark'\]\s+\.mantine-Input-input:focus-within\s*\{\s*--input-bd:\s*var\(--ecr-focus\);\s*\}/,
    );
  });

  it('поле з помилкою лишається червоним у фокусі (специфічніше правило повертає колір помилки)', () => {
    expect(css).toMatch(
      /:root\[data-mantine-color-scheme='dark'\]\s+\[data-error\]\s+\.mantine-Input-input:focus[^{]*\{\s*--input-bd:\s*var\(--mantine-color-error\);\s*\}/,
    );
  });
});
