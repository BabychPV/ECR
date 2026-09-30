import { readFileSync } from 'node:fs';
import path from 'node:path';

/**
 * Мінімальний резолвер каскаду CSS для перевірки контрасту комірок сітки.
 *
 * ⛔ Навіщо він узагалі потрібен, хоч контраст уже перевіряється в
 * `shared/theme/__tests__/contrast.test.ts`. Той тест бере пару «фон стану» +
 * `themeSurface.dark.text` — і це ПРИПУЩЕННЯ, а не факт. Насправді колір
 * тексту комірки задає не наша тема, а пакет: `revo-grid[theme=compact]
 * revogr-data .rgCell { color: rgba(0, 0, 0, 0.87) }`. У темній темі це давало
 * 1.35:1 — введене оператором значення було невидиме, — а тест лишався
 * зеленим, бо перевіряв колір, якого на екрані немає.
 *
 * ⛔ Тому тут читається САМЕ той CSS, що постачається (`node_modules`), а не
 * його переказ. Переказ розійшовся б із пакетом при першому ж оновленні
 * версії, і перевірка знову доводила б власне припущення.
 *
 * ⚠ Чому не `getComputedStyle` в jsdom: jsdom не реалізує ані каскад за
 * специфічністю для довільних селекторів, ані розкладку, потрібну axe для
 * правила `color-contrast` (`test/a11y.ts` це вже фіксує). Резолвер нижче
 * навмисно вузький — рівно те, що зустрічається в цих двох таблицях стилів:
 * тип, клас, атрибут, `:root`, нащадок. Селектор із псевдокласом
 * (`:hover`, `::before`) до статичного стану комірки не застосовний і
 * відкидається цілком, а не «майже правильно» матчиться.
 */

/** Елемент у ланцюжку предків: тип, атрибути, класи. */
export interface El {
  readonly tag: string;
  readonly attrs?: Readonly<Record<string, string>>;
  readonly classes?: readonly string[];
  readonly isRoot?: boolean;
}

interface Rule {
  readonly selector: string;
  readonly decls: ReadonlyMap<string, string>;
}

/** Специфічність без ідентифікаторів: [класи+атрибути+псевдокласи, типи]. */
type Specificity = readonly [number, number];

const WebRoot = process.cwd();

/** CSS пакета RevoGrid — той самий файл, що потрапляє в застосунок. */
export const PackageCss = readFileSync(
  path.resolve(
    WebRoot,
    'node_modules/@revolist/revogrid/dist/collection/components/revoGrid/revo-grid-style.css',
  ),
  'utf8',
);

/** CSS редактора комірки — окремий файл того самого пакета. */
export const PackageEditorCss = readFileSync(
  path.resolve(
    WebRoot,
    'node_modules/@revolist/revogrid/dist/collection/components/editors/revogr-edit-style.css',
  ),
  'utf8',
);

/**
 * CSS шапки — ще один файл того самого пакета.
 *
 * ⚠ Він потрібен у каскаді з тієї самої причини, що й два вище: вигляд
 * `.rgHeaderCell` складають ОБИДВА файли пакета (`revogr-header-style.css`
 * задає `display: flex` і `align-*`, `revo-grid-style.css` — тему `compact`),
 * і перевірка, що наше правило їх переважає, без одного з них доводила б
 * менше, ніж стверджує.
 */
export const PackageHeaderCss = readFileSync(
  path.resolve(
    WebRoot,
    'node_modules/@revolist/revogrid/dist/collection/components/header/revogr-header-style.css',
  ),
  'utf8',
);

/** Наш CSS станів комірки. */
export const AppCss = readFileSync(
  path.resolve(WebRoot, 'src/shared/theme/cell-states.css'),
  'utf8',
);

/** Розбирає таблицю стилів на плоский перелік правил. */
export function parseCss(css: string): Rule[] {
  const clean = css.replace(/\/\*[\s\S]*?\*\//g, '');
  const rules: Rule[] = [];

  for (const block of clean.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const decls = new Map<string, string>();

    for (const line of (block[2] ?? '').split(';')) {
      const at = line.indexOf(':');
      if (at < 0) continue;

      const prop = line.slice(0, at).trim().toLowerCase();
      if (prop.length === 0) continue;

      // ⚠ `background-color` і `background` зводяться до одного ключа: вони
      // змагаються за той самий піксель, і розділені вони дали б два
      // «переможці» там, де переможець рівно один.
      decls.set(prop === 'background' ? 'background-color' : prop, line.slice(at + 1).trim());
    }

    if (decls.size > 0) {
      for (const selector of (block[1] ?? '').split(',')) {
        if (selector.trim().length > 0) rules.push({ selector: selector.trim(), decls });
      }
    }
  }

  return rules;
}

/**
 * Каскад: правила ПАКЕТА йдуть першими навмисно.
 *
 * ⛔ При однаковій специфічності перемагає те правило, що оголошене пізніше, а
 * порядок вставки стилів Stencil у `<head>` відносно нашого бандла не
 * гарантований. Тому тут при рівності перемагає ПАКЕТ — песимістичне
 * припущення: наше перевизначення мусить вигравати специфічністю СТРОГО, а не
 * сподіватися на порядок завантаження.
 */
export function gridCascade(): Rule[] {
  return [
    ...parseCss(PackageCss),
    ...parseCss(PackageEditorCss),
    ...parseCss(PackageHeaderCss),
    ...parseCss(AppCss),
  ];
}

/** Токени одного компаунда селектора. */
const CompoundToken = /([a-zA-Z][\w-]*)|(\.[\w-]+)|(\[[^\]]+\])|(::?[\w-]+)/g;

/** Чи відповідає один компаунд одному елементу; `null` — селектор непридатний. */
function matchesCompound(compound: string, el: El): boolean | null {
  let ok = true;

  for (const token of compound.matchAll(CompoundToken)) {
    const [text] = token;

    if (text.startsWith('::')) return null;

    if (text.startsWith(':')) {
      // Єдиний псевдоклас, що описує статичний стан. Решта (`:hover`,
      // `:not`, `:focus`) до комірки в спокої не застосовна — і селектор із
      // ними відкидається цілком, а не матчиться «десь поруч».
      if (text !== ':root') return null;
      if (el.isRoot !== true) ok = false;
      continue;
    }

    if (text.startsWith('.')) {
      if (!(el.classes ?? []).includes(text.slice(1))) ok = false;
      continue;
    }

    if (text.startsWith('[')) {
      const attr = /^\[\s*([\w-]+)\s*(?:([~^|$*]?=)\s*['"]?([^'"\]]*)['"]?\s*)?\]$/.exec(text);
      if (attr === null) return null;

      const name = attr[1] ?? '';
      const actual = el.attrs?.[name];

      if (actual === undefined) ok = false;
      else if (attr[2] === '=' && actual !== attr[3]) ok = false;
      else if (attr[2] === '^=' && !actual.startsWith(attr[3] ?? '')) ok = false;
      else if (attr[2] !== undefined && !['=', '^='].includes(attr[2])) return null;

      continue;
    }

    if (text !== el.tag) ok = false;
  }

  return ok;
}

/** Специфічність компаунда. */
function specificityOf(selector: string): Specificity {
  let classes = 0;
  let types = 0;

  for (const token of selector.matchAll(CompoundToken)) {
    const [text] = token;
    if (text.startsWith('::')) types += 1;
    else if (text.startsWith(':') || text.startsWith('.') || text.startsWith('[')) classes += 1;
    else types += 1;
  }

  return [classes, types];
}

/**
 * Чи відповідає селектор ланцюжку предків (останній елемент — цільовий).
 *
 * ⚠ `>` зводиться до нащадка: у цих двох таблицях стилів немає жодного
 * випадку, де різниця між прямим і непрямим нащадком змінила б результат для
 * комірки, — а повний матчер тут був би окремою бібліотекою з власними вадами.
 */
function matches(selector: string, chain: readonly El[]): boolean {
  const compounds = selector.replace(/\s*>\s*/g, ' ').trim().split(/\s+/);

  let index = chain.length - 1;
  for (let i = compounds.length - 1; i >= 0; i -= 1) {
    const compound = compounds[i] ?? '';

    if (i === compounds.length - 1) {
      const hit = matchesCompound(compound, chain[index] as El);
      if (hit === null || !hit) return false;
      index -= 1;
      continue;
    }

    let found = false;
    while (index >= 0) {
      const hit = matchesCompound(compound, chain[index] as El);
      index -= 1;
      if (hit === null) return false;
      if (hit) {
        found = true;
        break;
      }
    }

    if (!found) return false;
  }

  return true;
}

/** Значення властивості, що виграє каскад; `null` — не оголошена ніде. */
export function declared(
  rules: readonly Rule[],
  chain: readonly El[],
  property: string,
): string | null {
  let best: string | null = null;
  let bestSpec: Specificity = [-1, -1];

  for (const rule of rules) {
    const value = rule.decls.get(property);
    if (value === undefined) continue;
    if (!matches(rule.selector, chain)) continue;

    const spec = specificityOf(rule.selector);

    // Строго більша специфічність — див. `gridCascade`: при рівності
    // лишається той, кого вже знайшли, тобто пакет.
    if (spec[0] > bestSpec[0] || (spec[0] === bestSpec[0] && spec[1] > bestSpec[1])) {
      best = value;
      bestSpec = spec;
    }
  }

  return best;
}

/** Розкриває `var(--x)` через значення, оголошені на кореневому елементі. */
export function resolveVar(
  rules: readonly Rule[],
  root: El,
  value: string,
  depth = 0,
): string | null {
  const use = /^var\(\s*(--[\w-]+)\s*(?:,\s*([^)]+))?\)$/.exec(value.trim());
  if (use === null) return value.trim();
  if (depth > 8) return null;

  const resolved = declared(rules, [root], use[1] ?? '');
  if (resolved === null) return use[2] === undefined ? null : resolveVar(rules, root, use[2], depth + 1);

  return resolveVar(rules, root, resolved, depth + 1);
}
