import type { CSSVariablesResolver, MantineTheme } from '@mantine/core';
import { flatten, parseColor } from './contrast';
import { brandTextOnDark, surfaces } from './theme';

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

/**
 * Відтінок `brand` для акцентного ТЕКСТУ у світлій схемі. У темній — не індекс
 * кортежу, а `brandTextOnDark` (`theme.ts`): одне значення на текст, межу й
 * посилання, замінюване одним рядком.
 */
const AccentTextShade = { light: 6 } as const;

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

/** `rgba(...)` із hex і непрозорості — те саме, що `alpha()` у `@mantine/core`. */
function withAlpha(hex: string, a: number): string {
  const { r, g, b } = parseColor(hex);

  return `rgba(${r}, ${g}, ${b}, ${a})`;
}

/**
 * Текст і межа `variant="outline"` статусних кольорів — темна схема.
 *
 * ⛔ Mantine бере `--mantine-color-<c>-outline` за індексом
 * `max(primaryShade.dark − 4, 0)` = `[1]` — `#ffe3e3`/`#ffe8cc`/`#d3f9d8`, тобто
 * той самий майже білий, що й `light-color` до `statusLightColor`. Коментар у
 * `contrast.test.ts` називав `[1]` «блідим навмисно, щоб не зникнути й не
 * різати очі», але це опис дефолту Mantine, а не рішення: `theme.ts` прямо
 * каже, що індекси 0–4 «не задіяні жодним поточним використанням». `[3]` —
 * так само блідий кінець шкали (не заливковий `[5]`), тож обидві причини з
 * того коментаря він задовольняє: 8.16–12.46 на всіх поверхнях темної схеми.
 *
 * ⚠ `-outline-hover` (тло наведення) перевизначається разом: Mantine рахує
 * його з того самого індексу, і лишити `[1]` означало б наведення іншого
 * відтінку, ніж межа.
 */
function statusOutline(theme: MantineTheme, scheme: Scheme): Record<string, string> {
  if (scheme !== 'dark') return {};

  const out: Record<string, string> = {};
  for (const name of StatusColors) {
    const shade = theme.colors[name]?.[StatusShade.dark];
    if (shade === undefined) continue;
    out[`--mantine-color-${name}-outline`] = shade;
    out[`--mantine-color-${name}-outline-hover`] = withAlpha(shade, 0.05);
  }

  return out;
}

/**
 * Тло `variant="light"` і наведення `subtle` статусних кольорів — світла схема.
 *
 * ⛔ Mantine кладе `alpha([6], 0.1)` / `alpha([6], 0.12)` поверх поверхні. На
 * `sunken` (`#eef0f5`) текст `[6]` на цьому композиті давав помилці 4.11/3.99,
 * успіху 4.50/4.36. Альфою цього не виправити: сам `[6]` помилки на `sunken`
 * дає лише 4.79, тож прохідна альфа (< 0.03) робить тло сірим, а не статусним.
 *
 * ⚠ Тому тло НЕПРОЗОРЕ: `[0]` — фон, половина між `[0]` і `[1]` — наведення.
 * Воно не залежить від поверхні, отже й контраст один на всіх чотирьох:
 * помилка 5.10/4.80, попередження 5.83/5.57, успіх 5.46/5.28. `[1]` цілком
 * для наведення помилки дав би 4.51 — рівно на межі, від чого й тікаємо.
 */
function statusLightBackground(theme: MantineTheme, scheme: Scheme): Record<string, string> {
  if (scheme !== 'light') return {};

  const out: Record<string, string> = {};
  for (const name of StatusColors) {
    const tuple = theme.colors[name];
    if (tuple === undefined) continue;
    out[`--mantine-color-${name}-light`] = tuple[0];
    out[`--mantine-color-${name}-light-hover`] = flatten(withAlpha(tuple[1], 0.5), tuple[0]);
    // Той самий дефект на наведенні `outline`: `alpha([6], 0.05)` на `sunken`
    // дає помилці 4.44. Непрозоре `[0]` — 5.10 на кожній поверхні.
    out[`--mantine-color-${name}-outline-hover`] = tuple[0];
  }

  return out;
}

/**
 * `brand` як текст, межа й посилання — темна схема.
 *
 * ⛔ Дефолт Mantine за `primaryShade.dark = 5`: текст `light`/`subtle` —
 * `brand[0]` (`#f4f4fc`), `outline` — `brand[1]` (`#e7e8f8`), тобто майже білий
 * без фірмового відтінку; посилання й `c="brand"` — `brand[4]` (4.34 на
 * `raised`, нижче AA). Усі п'ять змінних вирівняно на ОДНЕ значення
 * `brandTextOnDark`. Заливка `filled` (`brand[5]`, білий 5.81) і кільце
 * фокуса (`brand[4]`, `motion.css`) не змінюються.
 */
function brandOnDark(scheme: Scheme): Record<string, string> {
  if (scheme !== 'dark') return {};

  return {
    '--mantine-color-brand-light-color': brandTextOnDark,
    '--mantine-color-brand-outline': brandTextOnDark,
    '--mantine-color-brand-outline-hover': withAlpha(brandTextOnDark, 0.05),
    '--mantine-color-brand-text': brandTextOnDark,
    '--mantine-color-anchor': brandTextOnDark,
  };
}

/** Змінні однієї схеми. */
function schemeVariables(theme: MantineTheme, scheme: Scheme): Record<string, string> {
  const s = surfaces[scheme];

  const brand = theme.colors['brand'] ?? [];
  const success = theme.colors['statusSuccess'] ?? [];
  const warning = theme.colors['statusWarning'] ?? [];
  const danger = theme.colors['statusError'] ?? [];

  const accent = brand[AccentShade[scheme]] ?? s.text;
  const accentText = scheme === 'dark' ? brandTextOnDark : (brand[AccentTextShade.light] ?? s.text);
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
    // Текст/межа `outline` (темна) і тло `light` (світла) — причини там само.
    ...statusOutline(theme, scheme),
    ...statusLightBackground(theme, scheme),
    // `brand` як текст/межа/посилання в темній — причина в `brandOnDark`.
    ...brandOnDark(scheme),

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
