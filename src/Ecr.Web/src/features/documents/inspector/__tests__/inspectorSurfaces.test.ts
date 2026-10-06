import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { surfaces } from '@/shared/theme/theme';
import { contrast } from '@/shared/theme/contrast';

/**
 * Тло чипа адреси, заголовка групи й підсвітки задачі в інспекторі
 * (живий прогін batch-2-a, дефект 5).
 *
 * Було `--mantine-color-default-hover`: у темній темі це сірий Mantine `dark-5`
 * (#3b3b3b), якого немає серед поверхонь теми, і приглушений текст на ньому мав
 * 4.34:1. Тепер — токени теми з макета (`--sunken`, `--hover`), і контраст
 * приглушеного тексту на них рахується тут для обох тем.
 *
 * ⚠ jsdom зовнішніх стилів не застосовує, тому читається сам файл (як
 * `stickyHeadSurface.test.ts`).
 */
const css = readFileSync(
  path.resolve(process.cwd(), 'src/features/documents/inspector/inspector.css'),
  'utf8',
).replace(/\/\*[\s\S]*?\*\//g, '');

function background(selector: string): string {
  const start = css.indexOf(`${selector} {`);
  if (start < 0) throw new Error(`У inspector.css немає правила ${selector}`);

  const body = css.slice(css.indexOf('{', start) + 1, css.indexOf('}', start));

  return /(?:^|;|\n)\s*background\s*:([^;]+)/.exec(body)?.[1]?.trim() ?? '';
}

const TokenToSurface = { 'var(--ecr-sunken)': 'sunken', 'var(--ecr-hover)': 'hover' } as const;

describe('Інспектор: тло під приглушеним текстом — поверхні теми з контрастом ≥ 4.5', () => {
  it.each(['.ecr-insp-chip', '.ecr-insp-group', '.ecr-insp-issue:not(:disabled):hover'])('%s', (selector) => {
    const value = background(selector);

    expect(value, 'сірий Mantine замість поверхні теми').not.toContain('--mantine-color-default-hover');
    expect(Object.keys(TokenToSurface)).toContain(value);

    const surface = TokenToSurface[value as keyof typeof TokenToSurface];
    for (const scheme of ['light', 'dark'] as const) {
      expect(contrast(surfaces[scheme].muted, surfaces[scheme][surface])).toBeGreaterThanOrEqual(4.5);
    }
  });
});
