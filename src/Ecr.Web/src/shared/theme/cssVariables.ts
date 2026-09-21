import type { CSSVariablesResolver, MantineTheme } from '@mantine/core';
import { surfaces } from './theme';

/**
 * Токени макета → CSS-змінні (`UI-01`, директива №15 §1).
 *
 * ⛔ Навіщо резолвер, коли є `theme.ts`. Mantine сам розкладає в CSS лише те,
 * про що знає: кольори, відступи, розміри тексту. «Чотирьох рівнів поверхні»,
 * `select`, `hover`, `hatch` у ньому немає в принципі, а CSS не читає
 * TypeScript. Без цього мосту кожен новий екран писав би свій `#f6f7fa` — і
 * рівно від цього застерігає `ФВ-14.11`.
 *
 * ⛔ **Джерело значень одне — `theme.ts`.** Тут немає жодного літерала кольору:
 * поверхні беруться з `surfaces`, акцент і фокус — з `theme.colors.brand`,
 * статуси — з `theme.colors.status*`. Саме тому відповідь замовника на
 * `Q15-01b` (фірмові кольори) коштуватиме десяти рядків у `brand`, а не
 * переробки: усе, що нижче, перерахується само.
 *
 * ⚠ Чому статусні `--ecr-*` виводяться з чинних кортежів, а не з макета.
 * Макетні `--success/--warning/--danger` не проходять того, що вже стережеться:
 * у темній темі `#4cc38a`/`#e3b341`/`#f97066` дають білому написові 2.22 / 1.95
 * / 2.79 — тобто як заливку `filled` їх використати не можна, а саме так їх
 * застосовує Mantine за `primaryShade: { dark: 5 }`. Директива §1 задає правило
 * прямо: «правий той, що проходить `contrast.test.ts`». Виміряні числа чинних
 * кортежів — у звіті PR і в `__tests__/cssVariables.test.ts`.
 */

/** Індекс відтінку `brand`, з якого береться акцент у кожній схемі. */
const AccentShade = { light: 6, dark: 5 } as const;

/** Відтінок `brand` для акцентного ТЕКСТУ (на темному потрібен блідіший). */
const AccentTextShade = { light: 6, dark: 3 } as const;

/** Відтінок `brand` для кільця фокуса — той самий, що бере `motion.css`. */
const FocusShade = { light: 6, dark: 4 } as const;

/**
 * Відтінок статусного кортежу для тексту статусу.
 *
 * ⚠ 6 у світлій — той самий індекс, що бере `variant="filled"`/`outline`
 * (`primaryShade.light`). У темній — 3, а не 5: на темному тлі заливковий
 * відтінок (`#c92a2a` для помилки) дає 2.2:1, тобто напис зникає. Індекс 3 —
 * блідий кінець шкали, виміряний проти темного `ground`: 12.46 / 11.43 / 9.99.
 */
const StatusShade = { light: 6, dark: 3 } as const;

type Scheme = keyof typeof surfaces;

/** Статусні кольори теми, чий `variant="light"`/`"subtle"` фарбує текст. */
const StatusColors = ['statusError', 'statusWarning', 'statusSuccess'] as const;

/**
 * Текст `variant="light"`/`"subtle"` статусних кольорів — темна схема.
 *
 * ⛔ Mantine бере цей текст за індексом `max(primaryShade.dark − 5, 0)`
 * (`getCSSColorVariables` у `@mantine/core`). За `primaryShade.dark = 5` це
 * `[0]` — `#fff5f5`/`#fff4e6`/`#ebfbee`, тобто майже білий: кнопка
 * `color="statusError" variant="subtle"` у темній темі читалася як звичайний
 * текст, а не як помилка. Контраст при цьому був високий (9–17), тому жоден
 * поріг AA дефекту не бачив — ламався не контраст, а сам статусний відтінок.
 *
 * ⚠ Лише темна схема: у світлій Mantine бере `primaryShade.light` = 6, тобто
 * рівно `StatusShade.light` — перевизначати нічого.
 */
function statusLightColor(theme: MantineTheme, scheme: Scheme): Record<string, string> {
  if (scheme !== 'dark') return {};

  const out: Record<string, string> = {};
  for (const name of StatusColors) {
    const shade = theme.colors[name]?.[StatusShade.dark];
    if (shade !== undefined) out[`--mantine-color-${name}-light-color`] = shade;
  }

  return out;
}

/** Змінні однієї схеми. */
function schemeVariables(theme: MantineTheme, scheme: Scheme): Record<string, string> {
  const s = surfaces[scheme];

  const brand = theme.colors['brand'] ?? [];
  const success = theme.colors['statusSuccess'] ?? [];
  const warning = theme.colors['statusWarning'] ?? [];
  const danger = theme.colors['statusError'] ?? [];

  const accent = brand[AccentShade[scheme]] ?? s.text;
  const accentText = brand[AccentTextShade[scheme]] ?? s.text;
  const focus = brand[FocusShade[scheme]] ?? s.text;

  return {
    /*
     * ⛔ Перевизначення самої Mantine — чотири змінні, і кожна має причину:
     * без них тло сторінки, основний і приглушений текст та межа поля лишилися
     * б дефолтними (`#fff`/`#242424`, `#000`/`#c9c9c9`, `#868e96`, `#ced4da`),
     * тобто макет діяв би лише там, де хтось явно написав `var(--ecr-*)`.
     */
    '--mantine-color-body': s.ground,
    '--mantine-color-text': s.text,
    '--mantine-color-dimmed': s.muted,

    /*
     * ⚠ `borderStrong`, а не `border`: ця змінна фарбує межу ПОЛЯ ВВОДУ, тобто
     * межу елемента керування, а не роздільник. Роздільникам лишається
     * `--ecr-border`. Числа й межа цієї правки — у коментарі до `surfaces`.
     */
    '--mantine-color-default-border': s.borderStrong,

    // Текст `light`/`subtle` статусних кольорів — причина в `statusLightColor`.
    ...statusLightColor(theme, scheme),

    // Поверхні: чотири рівні глибини.
    '--ecr-ground': s.ground,
    '--ecr-surface': s.surface,
    '--ecr-sunken': s.sunken,
    '--ecr-raised': s.raised,

    // Лінії.
    '--ecr-border': s.border,
    '--ecr-border-strong': s.borderStrong,
    '--ecr-grid-line': s.gridLine,

    // Текст трьох рівнів; `faint` — лише неконтентне (див. `surfaces`).
    '--ecr-text': s.text,
    '--ecr-muted': s.muted,
    '--ecr-faint': s.faint,

    // Акцент і виділення — виводяться з `brand`, не дублюють його.
    '--ecr-accent': accent,
    '--ecr-accent-text': accentText,
    '--ecr-accent-soft': s.accentSoft,
    '--ecr-select': s.select,
    '--ecr-hover': s.hover,
    '--ecr-focus': focus,

    // Статуси — з чинних кортежів (причина вище).
    '--ecr-success': success[StatusShade[scheme]] ?? s.text,
    '--ecr-warning': warning[StatusShade[scheme]] ?? s.text,
    '--ecr-danger': danger[StatusShade[scheme]] ?? s.text,

    // Сітка: тло обчисленої комірки і штриховка закритої.
    '--ecr-calc-bg': s.calcBg,
    '--ecr-hatch': s.hatch,
  };
}

/**
 * Резолвер для `MantineProvider`.
 *
 * ⚠ `variables` порожній навмисно: кожен токен нижче має ДВА значення — світле
 * й темне, — і жодного, що не залежить від схеми. Покласти щось у `variables`
 * означало б, що воно не перемкнеться разом із темою, і помітно це стане лише
 * на екрані.
 */
export const cssVariablesResolver: CSSVariablesResolver = (theme) => ({
  variables: {},
  light: schemeVariables(theme, 'light'),
  dark: schemeVariables(theme, 'dark'),
});
