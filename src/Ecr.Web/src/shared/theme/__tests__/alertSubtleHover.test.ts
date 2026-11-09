import { readFileSync } from 'node:fs';
import path from 'node:path';
import { DEFAULT_THEME, defaultCssVariablesResolver, mergeMantineTheme } from '@mantine/core';
import { describe, expect, it } from 'vitest';
import { AA, contrast, flatten } from '../contrast';
import { cssVariablesResolver } from '../cssVariables';
import { surfaces, theme } from '../theme';

/**
 * ✎ debt-1109 b+ (F13 p212, P3): у темній темі `subtle`-кнопка «Hide rows» у `light`-банері мала на наведенні
 * 4.42:1 — тло `brand-light-hover` кнопки лягало поверх тла банера `brand-light`. Тепер наведення в банері —
 * підкреслення без тла (`controls.css`), і текст лишається на тлі банера.
 *
 * ⚠ jsdom зовнішніх стилів не застосовує, тому правило читається з файлу (як `inspectorSurfaces.test.ts`), а
 * контраст рахується з тих самих змінних, що їх віддає `cssVariablesResolver`.
 */
const css = readFileSync(path.resolve(process.cwd(), 'src/shared/theme/controls.css'), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');

function dark(): Record<string, string> {
  const full = mergeMantineTheme(DEFAULT_THEME, theme);
  const base = defaultCssVariablesResolver(full);
  const ours = cssVariablesResolver(full);

  return { ...base.variables, ...base.dark, ...ours.variables, ...ours.dark };
}

function deref(vars: Record<string, string>, name: string): string {
  let value = vars[name] ?? '';
  for (let depth = 0; depth < 10; depth++) {
    const ref = /^var\((--[\w-]+)\)$/.exec(value.trim());
    if (ref === null) break;
    value = vars[ref[1] as string] ?? '';
  }

  return value;
}

describe('b+: наведення subtle-кнопки в light-банері темної теми', () => {
  it('правило controls.css: без власного тла, з підкресленням', () => {
    const selector =
      "[data-mantine-color-scheme='dark'] .mantine-Alert-root .mantine-Button-root[data-variant='subtle']:not(:disabled, [data-disabled]):hover";
    const start = css.indexOf(`${selector} {`);

    expect(start, 'немає правила наведення subtle у банері').toBeGreaterThanOrEqual(0);
    const body = css.slice(css.indexOf('{', start) + 1, css.indexOf('}', start));
    expect(body).toMatch(/background\s*:\s*transparent/u);
    expect(body).toMatch(/text-decoration\s*:\s*underline/u);
  });

  it('текст кнопки на тлі банера ≥ 4.5 на кожній поверхні; старий композит (тло наведення поверх банера) — нижче', () => {
    const vars = dark();
    const text = deref(vars, '--mantine-color-brand-light-color');
    let stackedWorst = Infinity;

    for (const page of ['surface', 'raised', 'sunken'] as const) {
      const banner = flatten(deref(vars, '--mantine-color-brand-light'), surfaces.dark[page]);
      expect(contrast(text, banner), `текст на банері поверх ${page}`).toBeGreaterThanOrEqual(AA.text);
      stackedWorst = Math.min(stackedWorst, contrast(text, flatten(deref(vars, '--mantine-color-brand-light-hover'), banner)));
    }

    // Саме цей композит і давав 4.42 — тому тло наведення в банері прибрано.
    expect(stackedWorst).toBeLessThan(AA.text);
  });
});
