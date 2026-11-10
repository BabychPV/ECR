import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { DEFAULT_THEME, mergeMantineTheme } from '@mantine/core';
import { contrast } from '@/shared/theme/contrast';
import { cssVariablesResolver } from '@/shared/theme/cssVariables';
import { theme } from '@/shared/theme/theme';

/**
 * ✎ 10.11, дефект людини (P2): смуга 12 місяців у переліку документів «не гарно виглядає і не рівно» — клітинка
 * «Late (flagged)» мала рамку 2px, а поточний період — зовнішнє кільце-тінь 2px, тож вони були більшими за сусідні
 * або зсунутими. Правило (KIT.md «MonthStrip»): УСІ стани — один зовнішній розмір; «late» і кільце — всередині.
 * jsdom верстки не має, тож правило доводимо в CSS; живу геометрію (rect ±0.5 px, 12 клітинок і легенда) міряє
 * `e2e/monthStripAlign.spec.ts`.
 *
 * ⛔ Червоний на коді до 10.11: `border-width: 2px` у «late», зовнішній `box-shadow` у поточного, `inline-grid` клітинки.
 */
const css = readFileSync(path.resolve(process.cwd(), 'src/features/documents/documentsList.css'), 'utf8').replace(
  /\/\*[\s\S]*?\*\//g,
  '',
);

interface Rule {
  readonly selector: string;
  readonly body: string;
}

const Rules: readonly Rule[] = [...css.matchAll(/([^{}]+)\{([^}]*)\}/g)].map((m) => ({
  selector: (m[1] ?? '').trim(),
  body: m[2] ?? '',
}));

/** Правила, що стосуються коробки клітинки у певному стані (`[data-cell=…]` / `[data-current=…]`). */
const StateRules = Rules.filter((r) => /\.ecr-year-strip-box\[data-(cell|current)/.test(r.selector));

function declaration(body: string, property: string): string | undefined {
  return new RegExp(`(?:^|;|\\s)${property}\\s*:\\s*([^;]+)`).exec(body)?.[1]?.trim();
}

/** Шари `box-shadow` — коми поза дужками (`var(--a, var(--b))` не ріже). */
function topLevelLayers(value: string): string[] {
  const layers: string[] = [];
  let depth = 0;
  let current = '';
  for (const ch of value) {
    if (ch === '(') depth += 1;
    if (ch === ')') depth -= 1;
    if (ch === ',' && depth === 0) {
      layers.push(current);
      current = '';
    } else {
      current += ch;
    }
  }
  layers.push(current);

  return layers;
}

function only(selector: string): string {
  const found = Rules.find((r) => r.selector === selector);
  if (found === undefined) throw new Error(`правила ${selector} немає в documentsList.css`);

  return found.body;
}

describe('смуга 12 місяців: стан не змінює розмір клітинки', () => {
  it('є правила для всіх станів, що їх має модель (late, submitted, approved, locked, notOpen, returned, current)', () => {
    const selectors = StateRules.map((r) => r.selector).join('\n');
    for (const state of ['late', 'submitted', 'approved', 'locked', 'notOpen', 'returned']) {
      expect(selectors).toContain(`[data-cell='${state}']`);
    }
    expect(selectors).toContain("[data-current='true']");
  });

  it.each(StateRules.map((r) => [r.selector, r.body] as const))(
    '%s: не міняє товщину рамки, розмір, поля чи відступи',
    (_selector, body) => {
      for (const property of ['border-width', 'border', 'width', 'height', 'min-width', 'min-height', 'margin', 'padding', 'font-size']) {
        expect(declaration(body, property), property).toBeUndefined();
      }
      // `border-top` / `border-left`… з товщиною — те саме, що `border-width`.
      expect(body).not.toMatch(/border-(top|right|bottom|left)(-width)?\s*:/);
    },
  );

  it.each(StateRules.map((r) => [r.selector, r.body] as const))('%s: тінь — лише всередині (inset)', (_selector, body) => {
    const shadow = declaration(body, 'box-shadow');
    if (shadow === undefined || shadow === 'none') return;
    for (const layer of topLevelLayers(shadow)) {
      expect(layer.trim(), 'зовнішня тінь робить клітинку візуально більшою').toMatch(/^inset\b/);
    }
  });

  it('поточний період — outline усередині клітинки (offset + товщина ≤ 0)', () => {
    const body = only(".ecr-year-strip-box[data-current='true']");
    const outline = declaration(body, 'outline') ?? '';
    const width = Number(/(\d+(?:\.\d+)?)px/.exec(outline)?.[1] ?? 'NaN');
    const offset = Number(/(-?\d+(?:\.\d+)?)px/.exec(declaration(body, 'outline-offset') ?? '')?.[1] ?? 'NaN');

    expect(outline).toMatch(/solid/);
    expect(width + offset, `outline ${outline}, offset ${String(offset)}`).toBeLessThanOrEqual(0);
  });

  it('коробка — border-box з рамкою 1px; клітинка-посилання блочна (не на базовій лінії тексту)', () => {
    const box = only('.ecr-year-strip-box');
    expect(declaration(box, 'box-sizing')).toBe('border-box');
    expect(declaration(box, 'border')).toMatch(/^1px solid/);
    expect(declaration(only('.ecr-year-strip-cell'), 'display')).toMatch(/^(grid|flex|block)$/);
    expect(declaration(only('.ecr-year-strip > li'), 'display')).toBe('block');
  });

  it.each(['light', 'dark'] as const)('схема «%s»: кільце поточного і рамка «late» ≥ 3:1 до фону', (scheme) => {
    const out = cssVariablesResolver(mergeMantineTheme(DEFAULT_THEME, theme));
    const vars: Record<string, string> = { ...out.variables, ...out[scheme] };
    const token = (selector: string, property: string): string =>
      /var\((--ecr-[\w-]+)/.exec(declaration(only(selector), property) ?? '')?.[1] ?? '(немає токена)';
    const ring = token(".ecr-year-strip-box[data-current='true']", 'outline');
    const late = token(".ecr-year-strip-box[data-cell='late']", 'border-color');
    for (const token of [ring, late]) {
      expect(contrast(vars[token] ?? '', vars['--ecr-surface'] ?? ''), `${scheme}: ${token} на --ecr-surface`).toBeGreaterThanOrEqual(3);
    }
  });
});
