/**
 * Відсікання з `@mantine/core/styles.css` правил тих компонентів Mantine, яких
 * у збірці немає взагалі (`D-132`, бюджет маршруту).
 *
 * ⚠ Навіщо. `styles.css` — стилі ВСІХ ~90 компонентів (30.4 КБ gzip), і він
 * їде у вхідний CSS, тобто в кожен із маршрутів. Застосунок малює десь 55 із
 * них; решта — `Slider`, `Stepper`, `Timeline`, `Chip`… — 7.5 КБ gzip, які
 * браузер завантажує на кожній сторінці й не застосовує ніде. Найважчі
 * маршрути стояли на 248.0 КБ із 250.
 *
 * ⛔ Чому НЕ пофайлові `@mantine/core/styles/<Компонент>.css`, які Mantine
 * пропонує саме для цього. Вони НЕ побайтна копія частин `styles.css`: там,
 * де `styles.css` має `calc(0.0625rem * var(--mantine-scale))`, пофайловий
 * `VisuallyHidden.css` має `1px`. Перехід на них змінив би правила, а не лише
 * їхню кількість. Тут правила, що лишаються, лишаються ДОСЛІВНО тими самими і
 * в тому самому порядку — каскад не змінюється.
 *
 * ⚠ Правило видаляється лише тоді, коли КОЖЕН його селектор посилається на
 * клас виключеного компонента. Змішане правило (`.m_живий, .m_мертвий`)
 * лишається цілим — консервативно, текст не переписується.
 *
 * ⛔ Помилка в переліку не мовчить: `vite.config.ts` після збирання шукає
 * класи виключених компонентів у ВСІХ JS-чанках і валить `npm run build`,
 * якщо хоч один із них ужито (`findPrunedClassesInUse`). Тобто перший же
 * `<Slider>` у коді червонить збірку з назвою компонента, а не тихо малює
 * його без стилів.
 */

/**
 * Компоненти Mantine, стилі яких відсікаються. Ім'я = файл
 * `@mantine/core/styles/<Ім'я>.css`, з якого береться перелік класів.
 *
 * ⚠ Перелік виміряний: це рівно ті файли, жоден клас яких не трапляється в
 * JS збірки (2026-09-21, `@mantine/core` 7.15.2). Щоб повернути компонент —
 * прибрати рядок; збірка сама скаже, який саме, якщо забути.
 */
export const UnusedMantineComponents: readonly string[] = [
  'Affix',
  'AngleSlider',
  'AspectRatio',
  'Avatar',
  'BackgroundImage',
  'Blockquote',
  'Chip',
  'Container',
  'Dialog',
  'Flex',
  'Grid',
  'Image',
  'Indicator',
  'Kbd',
  'LoadingOverlay',
  'Mark',
  'Pagination',
  'PinInput',
  'Radio',
  'RadioCard',
  'RadioIndicator',
  'Rating',
  'RingProgress',
  'SemiCircleProgress',
  'Slider',
  'Spoiler',
  'Stepper',
  'ThemeIcon',
  'Timeline',
  'Tree',
  'TypographyStylesProvider',
];

/**
 * Хешований клас Mantine (`m_` + 6–8 шістнадцяткових цифр). Сім знаків —
 * не рідкість (`m_a8645c2`), тож фіксована довжина 8 пропустила б частину.
 */
const MantineClass = /m_[0-9a-f]{6,8}(?![0-9a-z_-])/g;

/** Усі класи Mantine, згадані в тексті (CSS чи JS). */
export function mantineClassesIn(text: string): Set<string> {
  return new Set(text.match(MantineClass) ?? []);
}

/** Розбиття за роздільником лише на верхньому рівні (не всередині `()`/`[]`). */
function splitTopLevel(text: string, separator: string): string[] {
  const parts: string[] = [];
  let depth = 0;
  let current = '';

  for (const ch of text) {
    if (ch === '(' || ch === '[') depth++;
    if (ch === ')' || ch === ']') depth--;

    if (ch === separator && depth === 0) {
      parts.push(current);
      current = '';
    } else {
      current += ch;
    }
  }

  parts.push(current);
  return parts;
}

const withoutComments = (text: string): string => text.replace(/\/\*[\s\S]*?\*\//g, '');

/**
 * Прибирає з CSS правила, кожен селектор яких посилається на клас із `dead`,
 * і `@keyframes` із мертвим ім'ям; порожні після цього `@media`/`@supports`
 * зникають теж. Решта тексту повертається без жодної зміни.
 *
 * ⚠ Розбір — за фігурними дужками, без повноцінного парсера CSS. Для
 * `styles.css` Mantine цього досить: у ньому немає рядків із дужками всередині
 * (це перевіряє тест на справжньому файлі).
 */
export function pruneCss(css: string, dead: ReadonlySet<string>): string {
  const isDead = (selector: string): boolean =>
    [...mantineClassesIn(selector)].some((cls) => dead.has(cls));

  let out = '';
  let index = 0;

  while (index < css.length) {
    const open = css.indexOf('{', index);
    if (open < 0) {
      out += css.slice(index);
      break;
    }

    let depth = 1;
    let close = open + 1;
    while (depth > 0 && close < css.length) {
      if (css[close] === '{') depth++;
      else if (css[close] === '}') depth--;
      close++;
    }

    const prelude = css.slice(index, open);
    const body = css.slice(open + 1, close - 1);
    const head = withoutComments(prelude).trim();

    if (/^@(media|supports|layer|container)\b/.test(head)) {
      const inner = pruneCss(body, dead);
      if (withoutComments(inner).trim() !== '') out += `${prelude}{${inner}}`;
    } else if (/^@keyframes\b/.test(head)) {
      if (!isDead(head)) out += `${prelude}{${body}}`;
    } else if (!splitTopLevel(head, ',').every(isDead)) {
      out += `${prelude}{${body}}`;
    }

    index = close;
  }

  return out;
}

/** Класи з `dead`, які все ж ужито в наданому коді збірки. */
export function findPrunedClassesInUse(
  code: Iterable<string>,
  dead: ReadonlySet<string>,
): Set<string> {
  const found = new Set<string>();

  for (const text of code) {
    for (const cls of mantineClassesIn(text)) {
      if (dead.has(cls)) found.add(cls);
    }
  }

  return found;
}
