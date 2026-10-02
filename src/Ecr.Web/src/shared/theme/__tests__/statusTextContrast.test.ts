import { DEFAULT_THEME, defaultCssVariablesResolver, mergeMantineTheme } from '@mantine/core';
import { describe, expect, it } from 'vitest';
import { AA, contrast } from '../contrast';
import { cssVariablesResolver } from '../cssVariables';
import { surfaces, theme } from '../theme';

/**
 * Текст `c="statusError|statusWarning|statusSuccess"` і `c="dimmed"` читається на кожній поверхні в обох
 * схемах (`ФВ-14.17`, WCAG 1.4.3).
 *
 * ⚠ Сліпа пляма, яку закриває файл: `contrast.test.ts` міряє статусні кольори у варіантах КОМПОНЕНТІВ
 * (`filled`, `outline`, `light`, `subtle`), а голий текст `<Text c="statusWarning">` Mantine фарбує
 * ІНШОЮ змінною — `--mantine-color-<колір>-text` (світла схема — `filled`, темна — відтінок `[4]`).
 * Нові панелі пишуть ним причини («немає гранту» у розрізі доступу), а `c="dimmed"` — підказки
 * під кожною формою SMTP, шаблонів і розкладів. axe контраст у jsdom не рахує (`test/a11y.ts`), тож
 * без цього файлу таку пару не міряло ніщо.
 *
 * ⚠ Змінні — з того, що побачить браузер: дефолтний резолвер Mantine, поверх нього наш (порядок
 * `getMergedVariables`), з розгорнутими `var(...)`.
 *
 * ⛔ Мутаційний доказ (локально, 2026-10-02): `statusWarning[4]` → `#8a3008` (темний відтінок) — червоний
 * випадок «statusWarning», темна схема.
 */
type Scheme = 'light' | 'dark';

function mergedVars(scheme: Scheme): Record<string, string> {
  const full = mergeMantineTheme(DEFAULT_THEME, theme);
  const base = defaultCssVariablesResolver(full);
  const ours = cssVariablesResolver(full);

  return { ...base.variables, ...base[scheme], ...ours.variables, ...ours[scheme] };
}

function deref(vars: Record<string, string>, name: string): string {
  let value = vars[name];

  for (let depth = 0; depth < 10 && value !== undefined; depth++) {
    const ref = /^var\((--[\w-]+)\)$/.exec(value.trim());
    if (ref === null) return value.trim().toLowerCase();
    value = vars[ref[1]!];
  }

  throw new Error(`змінна «${name}» не розгортається в колір`);
}

const variables = [
  '--mantine-color-statusError-text',
  '--mantine-color-statusWarning-text',
  '--mantine-color-statusSuccess-text',
  '--mantine-color-dimmed',
] as const;

const cases = variables.flatMap((name) => (['light', 'dark'] as const).map((scheme) => [name, scheme] as const));

describe('голий статусний і приглушений текст — контраст на поверхнях', () => {
  it.each(cases)('%s, схема «%s»: ≥ AA на ground/surface/sunken/raised', (name, scheme) => {
    const text = deref(mergedVars(scheme), name);

    for (const page of ['ground', 'surface', 'sunken', 'raised'] as const) {
      const bg = surfaces[scheme][page];

      expect(contrast(text, bg), `${scheme}: ${name} (${text}) на ${page} (${bg})`).toBeGreaterThanOrEqual(AA.text);
    }
  });
});
