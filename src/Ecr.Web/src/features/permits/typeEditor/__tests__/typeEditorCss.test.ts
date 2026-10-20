import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * P2-N2: підказка «Unique rows…» у `.pt-below` не має бути прокручуваною областю (axe `scrollable-region-focusable` на 500 px).
 * jsdom не має розкладки, тож `scrollWidth` тут не виміряти - доводимо правило в CSS, а живу перевірку (computed style і
 * `scrollWidth <= clientWidth` на 500 px) робить e2e `permitTypeEditor.spec.ts`; axe у браузері треба повторити.
 *
 * Мутаційний доказ (вручну, відновлено): прибрати правило `.pt-below .mantine-Code-root` - тест червоний.
 */
const css = readFileSync(resolve(__dirname, '../typeEditor.css'), 'utf8');

describe('typeEditor.css: підказка ключа', () => {
  it('Code block у .pt-below переноситься і не прокручується', () => {
    const rule = /\.pt-below \.mantine-Code-root\s*\{([^}]*)\}/.exec(css);
    expect(rule, 'правила .pt-below .mantine-Code-root немає').not.toBeNull();
    const body = rule?.[1] ?? '';
    expect(body).toMatch(/white-space:\s*pre-wrap/);
    expect(body).toMatch(/overflow-wrap:\s*anywhere/);
    expect(body).toMatch(/overflow:\s*visible/);
  });
});
