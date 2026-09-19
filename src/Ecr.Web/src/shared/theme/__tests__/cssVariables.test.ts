import { DEFAULT_THEME, mergeMantineTheme } from '@mantine/core';
import { describe, it, expect } from 'vitest';
import { AA, contrast } from '../contrast';
import { cssVariablesResolver } from '../cssVariables';
import { brand, cellState, surfaces, theme } from '../theme';

/**
 * Токени макета доходять до CSS — і доходять ЧИТАБЕЛЬНИМИ (`UI-01`, `ФВ-14.17`).
 *
 * ⛔ Навіщо окремий набір, коли поруч уже є `contrast.test.ts`. Той рахує
 * контраст над ЗНАЧЕННЯМИ з `theme.ts` — тобто доводить, що в об'єкті лежать
 * добрі числа. Він лишиться зеленим, якщо резолвер не віддасть змінну взагалі,
 * віддасть її лише для однієї схеми або переплутає світлу з темною: на екрані
 * буде дефолт Mantine, а гейт — зелений. Тут перевіряється ВИВІД резолвера, і
 * пари беруться за ІМЕНАМИ змінних, а не за значеннями з теми.
 *
 * ⚠ Обидва боки, і другий важливіший за перший. Перший бік — зіпсований токен
 * (поверхню темної теми зробили світлою) мусить завалити перевірку контрасту.
 * Другий бік — ЗНИКЛА змінна теж мусить завалити її, а не «пройти, бо нічого
 * не порушено». Саме ця пастка щойно коштувала нам `expectFocusRing`: зонд не
 * знаходив елемента й повертав «порушень немає». Тому доступ до змінної йде
 * через `readVar()`, який КИДАЄ виняток на відсутньому імені, а не повертає
 * `undefined`; його поведінка перевіряється окремо, нижче.
 */

const Schemes = ['light', 'dark'] as const;
type Scheme = (typeof Schemes)[number];

/** Реальний вивід резолвера для однієї схеми, як його побачить браузер. */
function resolve(scheme: Scheme): Record<string, string> {
  const full = mergeMantineTheme(DEFAULT_THEME, theme);
  const out = cssVariablesResolver(full);

  // Порядок той самий, що в Mantine: спільні змінні, поверх них — схема.
  return { ...out.variables, ...out[scheme] };
}

/**
 * Значення змінної; відсутня змінна — ПАДІННЯ, не `undefined`.
 *
 * ⛔ Це і є другий бік доказу. Повернути `undefined` означало б, що будь-яка
 * перевірка нижче мовчки перетвориться на `expect(undefined)` — або впаде з
 * незрозумілим повідомленням, або (як `expectFocusRing`) взагалі порахує
 * відсутність носія за відсутність проблеми.
 */
function readVar(vars: Record<string, string>, name: string): string {
  const value = vars[name];

  if (value === undefined || value.trim().length === 0) {
    throw new Error(
      `резолвер не віддав «${name}» — це порушення, а не «нема чого перевіряти». ` +
        `Є: ${Object.keys(vars).sort().join(', ')}`,
    );
  }

  return value;
}

/** Усі імена, що їх зобов'язаний віддати резолвер для КОЖНОЇ схеми. */
const Required = [
  // Перевизначення самої Mantine.
  '--mantine-color-body',
  '--mantine-color-text',
  '--mantine-color-dimmed',
  '--mantine-color-default-border',

  // Поверхні.
  '--ecr-ground',
  '--ecr-surface',
  '--ecr-sunken',
  '--ecr-raised',

  // Лінії.
  '--ecr-border',
  '--ecr-border-strong',
  '--ecr-grid-line',

  // Текст.
  '--ecr-text',
  '--ecr-muted',
  '--ecr-faint',

  // Акцент і виділення.
  '--ecr-accent',
  '--ecr-accent-text',
  '--ecr-accent-soft',
  '--ecr-select',
  '--ecr-hover',
  '--ecr-focus',

  // Статуси.
  '--ecr-success',
  '--ecr-warning',
  '--ecr-danger',

  // Сітка.
  '--ecr-calc-bg',
  '--ecr-hatch',
] as const;

/** Поверхні, на яких у застосунку може опинитися текст. */
const Backdrops = [
  '--ecr-ground',
  '--ecr-surface',
  '--ecr-sunken',
  '--ecr-raised',
  '--ecr-select',
  '--ecr-hover',
  '--ecr-calc-bg',
  '--ecr-accent-soft',
] as const;

/** Поверхні-«сторінки»: на них лягає контентний текст і статуси. */
const Pages = ['--ecr-ground', '--ecr-surface', '--ecr-sunken', '--ecr-raised'] as const;

describe('UI-01: резолвер віддає токени макета обом схемам', () => {
  it.each(Schemes)('схема «%s»: жодна змінна не загубилася', (scheme) => {
    const vars = resolve(scheme);

    // ⛔ Саме `readVar`, а не `toHaveProperty`: та сама функція, через яку
    // йдуть усі виміри нижче, — інакше перелік і виміри могли б розійтися.
    for (const name of Required) {
      expect(() => readVar(vars, name), name).not.toThrow();
    }
  });

  it.each(Schemes)('схема «%s»: кожне значення — розбірний колір', (scheme) => {
    const vars = resolve(scheme);

    for (const name of Required) {
      // `contrast` кидає на нерозбірному і на напівпрозорому — і те, й те тут
      // дефект: напівпрозора поверхня зробила б усі виміри нижче вигадкою.
      expect(() => contrast(readVar(vars, name), '#ffffff'), name).not.toThrow();
    }
  });

  it.each(Schemes)('схема «%s»: контентний текст читається на кожній поверхні', (scheme) => {
    const vars = resolve(scheme);

    for (const bg of Backdrops) {
      for (const fg of ['--ecr-text', '--ecr-muted'] as const) {
        const value = contrast(readVar(vars, fg), readVar(vars, bg));

        expect(value, `${scheme}: ${fg} на ${bg}`).toBeGreaterThanOrEqual(AA.text);
      }
    }
  });

  it.each(Schemes)('схема «%s»: `faint` тримає поріг неконтентного на кожній поверхні', (scheme) => {
    const vars = resolve(scheme);

    // ⚠ `AA.nonText`, а не `AA.text`, і це не послаблення заради проходження:
    // `faint` за домовленістю директиви №15 §1 — лише роздільники й
    // плейсхолдер, тобто неконтентне. Саме тому макетне `#8b91a5` тут НЕ
    // прийняте: воно провалює навіть цей поріг (2.58–2.93 на всіх світлих
    // поверхнях, крім чистого білого).
    for (const bg of Backdrops) {
      const value = contrast(readVar(vars, '--ecr-faint'), readVar(vars, bg));

      expect(value, `${scheme}: --ecr-faint на ${bg}`).toBeGreaterThanOrEqual(AA.nonText);
    }
  });

  it.each(Schemes)('схема «%s»: акцентний текст і статуси читаються на сторінці', (scheme) => {
    const vars = resolve(scheme);

    for (const bg of Pages) {
      for (const fg of ['--ecr-accent-text', '--ecr-success', '--ecr-warning', '--ecr-danger'] as const) {
        const value = contrast(readVar(vars, fg), readVar(vars, bg));

        expect(value, `${scheme}: ${fg} на ${bg}`).toBeGreaterThanOrEqual(AA.text);
      }
    }
  });

  it.each(Schemes)('схема «%s»: кільце фокуса видно на кожній поверхні', (scheme) => {
    const vars = resolve(scheme);

    for (const bg of Pages) {
      const value = contrast(readVar(vars, '--ecr-focus'), readVar(vars, bg));

      expect(value, `${scheme}: --ecr-focus на ${bg}`).toBeGreaterThanOrEqual(AA.nonText);
    }
  });

  it.each(Schemes)('схема «%s»: напис на заливці акценту читається', (scheme) => {
    const vars = resolve(scheme);

    // Біла мітка на `variant="filled"` — та сама пара, що вже гейтиться в
    // `contrast.test.ts` через `brand[6]`/`brand[5]`; тут вона перевіряється з
    // боку ВИВОДУ, тобто ловить і те, що резолвер узяв не той відтінок.
    expect(
      contrast('#ffffff', readVar(vars, '--ecr-accent')),
      `${scheme}: білий напис на --ecr-accent`,
    ).toBeGreaterThanOrEqual(AA.text);
  });
});

describe('UI-01: вивід не розходиться з `theme.ts`', () => {
  it.each(Schemes)('схема «%s»: поверхні — рівно ті, що в `surfaces`', (scheme) => {
    const vars = resolve(scheme);
    const s = surfaces[scheme];

    // ⛔ Не косметика: резолвер, який віддає СВІТЛІ значення в темний блок,
    // проходить усі перевірки контрасту вище (світла пара контрастна сама
    // по собі) і при цьому робить темну тему білою.
    expect(readVar(vars, '--ecr-ground')).toBe(s.ground);
    expect(readVar(vars, '--ecr-surface')).toBe(s.surface);
    expect(readVar(vars, '--ecr-sunken')).toBe(s.sunken);
    expect(readVar(vars, '--ecr-raised')).toBe(s.raised);
    expect(readVar(vars, '--ecr-text')).toBe(s.text);
    expect(readVar(vars, '--ecr-muted')).toBe(s.muted);
    expect(readVar(vars, '--ecr-faint')).toBe(s.faint);
    expect(readVar(vars, '--ecr-grid-line')).toBe(s.gridLine);
    expect(readVar(vars, '--ecr-hatch')).toBe(s.hatch);
  });

  it.each(Schemes)('схема «%s»: власні змінні Mantine перебиті нашими', (scheme) => {
    const vars = resolve(scheme);

    // Без цих чотирьох рядків макет діяв би лише там, де хтось явно написав
    // `var(--ecr-*)`, а решта сторінки лишилася б на дефолтах Mantine.
    expect(readVar(vars, '--mantine-color-body')).toBe(readVar(vars, '--ecr-ground'));
    expect(readVar(vars, '--mantine-color-text')).toBe(readVar(vars, '--ecr-text'));
    expect(readVar(vars, '--mantine-color-dimmed')).toBe(readVar(vars, '--ecr-muted'));
    expect(readVar(vars, '--mantine-color-default-border')).toBe(
      readVar(vars, '--ecr-border-strong'),
    );
  });

  it('акцент виводиться з `brand`, а не дублює його літералом', () => {
    // ⛔ Умова `Q15-01b`: фірмовий колір має замінюватися в ОДНОМУ місці.
    // Якби резолвер ніс власну копію індиго, відповідь замовника коштувала б
    // двох правок, і друга неминуче відстала б.
    expect(readVar(resolve('light'), '--ecr-accent')).toBe(brand[6]);
    expect(readVar(resolve('dark'), '--ecr-accent')).toBe(brand[5]);
    expect(readVar(resolve('light'), '--ecr-focus')).toBe(brand[6]);
    expect(readVar(resolve('dark'), '--ecr-focus')).toBe(brand[4]);
  });

  it('схеми не збігаються: темний блок не є копією світлого', () => {
    const light = resolve('light');
    const dark = resolve('dark');

    // ⚠ Найдешевший спосіб зламати резолвер — повернути `light` обома полями.
    // Усі виміри контрасту при цьому лишаються зеленими.
    for (const name of ['--ecr-ground', '--ecr-surface', '--ecr-text', '--ecr-muted'] as const) {
      expect(readVar(light, name), name).not.toBe(readVar(dark, name));
    }
  });
});

describe('UI-01: нове тло сторінки не зіпсувало сітку', () => {
  /**
   * ⛔ `--mantine-color-body` зрушився з `#ffffff`/`#242424` на `ground`, а
   * межі станів комірки гейтяться (`cellStateMeasured.test.ts`, гейт 3) проти
   * `themeSurface`, тобто проти СТАРОГО тла. Ця перевірка закриває проміжок:
   * лінії мають лишатися видимими й на тому тлі, яке тепер справді на екрані.
   */
  it.each(Schemes)('схема «%s»: межі станів видно на новому тлі', (scheme) => {
    const vars = resolve(scheme);
    const names = Object.keys(cellState) as (keyof typeof cellState)[];

    for (const name of names) {
      for (const bg of ['--ecr-ground', '--ecr-surface'] as const) {
        const value = contrast(cellState[name][scheme].line, readVar(vars, bg));

        expect(value, `${scheme}: межа «${name}» на ${bg}`).toBeGreaterThanOrEqual(AA.nonText);
      }
    }
  });
});

describe('UI-01: сама перевірка ловить обидва боки', () => {
  /**
   * ⚠ Калібрування, без якого все вище могло б бути зеленим із неправильної
   * причини. Перевіряється не продукт, а лінійка: що `readVar` падає на
   * зниклій змінній і що вимір падає на зіпсованому значенні.
   */
  it('зникла змінна — падіння, а не «порушень немає»', () => {
    const vars = resolve('dark');
    const without = { ...vars };
    delete without['--ecr-surface'];

    expect(() => readVar(without, '--ecr-surface')).toThrow(/не віддав/);
  });

  it('порожнє значення — теж падіння', () => {
    expect(() => readVar({ '--ecr-surface': '   ' }, '--ecr-surface')).toThrow(/не віддав/);
  });

  it('зіпсована поверхня темної теми валить вимір контрасту', () => {
    const vars = { ...resolve('dark'), '--ecr-surface': '#ffffff' };

    // Рівно та мутація, що названа в описі PR: зробити темну поверхню світлою.
    // Текст темної теми (`#e6e8f0`) на білому дає ~1.2:1.
    expect(contrast(readVar(vars, '--ecr-text'), readVar(vars, '--ecr-surface'))).toBeLessThan(
      AA.text,
    );
  });

  it('обчислення контрасту дає відомі значення', () => {
    // Лінійка сама по собі: функція, що завжди повертає 21, зробила б усе
    // вище зеленим.
    expect(contrast('#000000', '#ffffff')).toBeCloseTo(21, 5);
    expect(contrast('#ffffff', '#ffffff')).toBeCloseTo(1, 5);
  });
});
