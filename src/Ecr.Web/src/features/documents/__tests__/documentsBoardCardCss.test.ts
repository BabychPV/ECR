import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { DEFAULT_THEME, mergeMantineTheme } from '@mantine/core';
import { contrast } from '@/shared/theme/contrast';
import { cssVariablesResolver } from '@/shared/theme/cssVariables';
import { theme } from '@/shared/theme/theme';

/**
 * Дефект людини 21.10: на картці дошки смужка аркушів — ряд порожніх пунктирних квадратів,
 * а підпис «0 of 19 approved» виходить за праву межу картки. jsdom верстки не має, тож правило
 * доводимо в CSS, а живу геометрію (bounding box ±1 px, 1440 і 500) міряє `e2e/documentsBoardCard.spec.ts`.
 *
 * ⛔ Мутаційний доказ (локально, відновлено): прибрати правило «Draft» або `text-overflow` / `flex-wrap`
 * з `documentsBoard.css` — відповідний тест червоний.
 */
const css = readFileSync(path.resolve(process.cwd(), 'src/features/documents/documentsBoard.css'), 'utf8').replace(
  /\/\*[\s\S]*?\*\//g,
  '',
);

function body(selector: string): string {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const found = new RegExp(`${escaped}\\s*\\{([^}]*)\\}`).exec(css)?.[1];
  if (found === undefined) throw new Error(`правила ${selector} немає в documentsBoard.css`);

  return found;
}

const Draft = ".ecr-board-card .ecr-segbar__sg[data-state='Draft'][data-tone='neutral']";

describe('дошка документів: смужка аркушів на картці', () => {
  it('чернетка — суцільна заливка токеном, не пунктир і не порожній фон', () => {
    const rule = body(Draft);

    expect(rule).toMatch(/border-style:\s*solid/);
    expect(rule).toMatch(/background:\s*var\(--ecr-[\w-]+\)/);
    expect(rule).not.toMatch(/dashed|transparent/);
  });

  it.each(['light', 'dark'] as const)('схема «%s»: заливка чернетки відрізняється від фону картки ≥ 3:1', (scheme) => {
    const out = cssVariablesResolver(mergeMantineTheme(DEFAULT_THEME, theme));
    const vars: Record<string, string> = { ...out.variables, ...out[scheme] };
    const fill = /background:\s*var\((--[\w-]+)\)/.exec(body(Draft))?.[1] ?? '';

    expect(contrast(vars[fill] ?? '', vars['--ecr-surface'] ?? ''), `${scheme}: ${fill} на --ecr-surface`).toBeGreaterThanOrEqual(3);
  });

  it('сегменти переносяться на другий рядок, а не виходять за картку', () => {
    expect(body('.ecr-board-card .ecr-segbar__segs')).toMatch(/flex-wrap:\s*wrap/);
    expect(body('.ecr-board-card .ecr-segbar')).toMatch(/flex-wrap:\s*wrap/);
    expect(body('.ecr-board-card .ecr-segbar')).toMatch(/max-width:\s*100%/);
  });

  it('підпис обрізається в межах картки: min-width: 0, ellipsis, overflow: hidden', () => {
    const rule = body('.ecr-board-card .ecr-segbar__label');

    expect(rule).toMatch(/min-width:\s*0/);
    expect(rule).toMatch(/max-width:\s*100%/);
    expect(rule).toMatch(/overflow:\s*hidden/);
    expect(rule).toMatch(/text-overflow:\s*ellipsis/);
  });
});
