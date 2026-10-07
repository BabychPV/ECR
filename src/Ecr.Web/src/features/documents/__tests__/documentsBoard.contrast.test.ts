import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { DEFAULT_THEME, mergeMantineTheme } from '@mantine/core';
import { AA, contrast } from '@/shared/theme/contrast';
import { cssVariablesResolver } from '@/shared/theme/cssVariables';
import { theme } from '@/shared/theme/theme';

/**
 * Прохід a11y batch-4: підпис порожньої колонки дошки («Nothing here») мав
 * `--ecr-faint` — 3.49:1 (light) і 4.30:1 (dark) на тлі сторінки, axe
 * `color-contrast` (serious). jsdom контраст не рахує, тож доказ — тут:
 * колір правила, розв'язаний резолвером теми, на поверхнях, де лежить дошка.
 */
const css = readFileSync(path.resolve(process.cwd(), 'src/features/documents/documentsBoard.css'), 'utf8').replace(
  /\/\*[\s\S]*?\*\//g,
  '',
);

function ruleColorVar(selector: string): string {
  const body = new RegExp(`${selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\s*\\{([^}]*)\\}`).exec(css)?.[1];
  const name = /(?:^|;|\s)color:\s*var\((--[\w-]+)\)/.exec(body ?? '')?.[1];
  if (name === undefined) throw new Error(`у правилі ${selector} немає color: var(--…)`);

  return name;
}

const out = cssVariablesResolver(mergeMantineTheme(DEFAULT_THEME, theme));

describe('дошка документів: текст порожньої колонки читається (WCAG 1.4.3)', () => {
  it.each(['light', 'dark'] as const)('схема «%s»: ≥ 4.5 на тлі сторінки й поверхні', (scheme) => {
    const vars: Record<string, string> = { ...out.variables, ...out[scheme] };
    const fg = vars[ruleColorVar('.ecr-board-col-empty')];
    expect(fg, 'резолвер не віддав колір тексту').toBeTruthy();

    for (const bg of ['--ecr-ground', '--ecr-surface']) {
      expect(contrast(fg ?? '', vars[bg] ?? ''), `${scheme}: на ${bg}`).toBeGreaterThanOrEqual(AA.text);
    }
  });
});
