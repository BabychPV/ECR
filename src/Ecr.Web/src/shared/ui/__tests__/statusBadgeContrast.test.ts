import { describe, expect, it } from 'vitest';
import { DEFAULT_THEME, mergeMantineTheme } from '@mantine/core';
import { AA, contrast, flatten } from '@/shared/theme/contrast';
import { cssVariablesResolver } from '@/shared/theme/cssVariables';
import { theme } from '@/shared/theme/theme';
import { statusTones, toneFills, type StatusTone, type ToneFill } from '@/shared/ui/StatusBadge';

/**
 * `UI-04`, контраст тонів статусу (`ФВ-14.17`).
 *
 * ⛔ Міряються НЕ вигадані значення, а ті самі змінні, які застосунок віддає
 * браузеру: `cssVariablesResolver` тут викликається справжній, зі справжньою
 * темою. Тест, що переписував би кольори поруч, перевіряв би власну копію —
 * рівно той дефект, від якого `contrast.test.ts` стереже `cell-states.css`.
 *
 * ⛔ І головне: «кольору немає» тут НЕ читається як «колір правильний».
 * Кожен пошук токена або повертає значення, або КИДАЄ з назвою токена й
 * схеми. Мовчазний `undefined` пройшов би через `contrast()` як виняток без
 * причини або — гірше — як пропущений випадок у циклі.
 */

const resolved = cssVariablesResolver(mergeMantineTheme(DEFAULT_THEME, theme));

const schemes = ['light', 'dark'] as const;
type Scheme = (typeof schemes)[number];

/** Значення токена `var(--ecr-*)` у схемі — або названа причина відмови. */
function tokenValue(reference: string, scheme: Scheme): string {
  const name = /^var\((--[a-z0-9-]+)\)$/.exec(reference)?.[1];

  if (name === undefined) {
    throw new Error(`тон посилається не на токен теми, а на «${reference}» — ФВ-14.11`);
  }

  // `CSSVariables` індексується шаблонним типом `--${string}`, а не `string`.
  const value = resolved[scheme][name as `--${string}`];

  if (value === undefined) {
    throw new Error(
      `токен ${name} не оголошений у cssVariablesResolver (схема «${scheme}») — ` +
        'тон лишився БЕЗ кольору, а не з правильним',
    );
  }

  return value;
}

/**
 * Заливка тону — або названа причина, чому її немає.
 *
 * ⛔ Не `toneFills[tone]` напряму: прибери хтось рядок, і вираз дав би
 * `undefined`, а звернення до його поля — `TypeError` без жодного натяку на
 * причину. Тут причина названа словами.
 */
function fillOf(tone: StatusTone): ToneFill {
  const fill: ToneFill | undefined = toneFills[tone];

  if (fill === undefined) {
    throw new Error(`тон «${tone}» не має заливки — колір ЗНИК, а не став правильним`);
  }

  return fill;
}

/** Поверхні, на яких бейдж реально лежить: тло сторінки і панель. */
function surfacesOf(scheme: Scheme): readonly [string, string] {
  return [tokenValue('var(--ecr-ground)', scheme), tokenValue('var(--ecr-surface)', scheme)];
}

describe('тони StatusBadge покриті токенами', () => {
  /*
   * ⛔ Перша й найважливіша перевірка: перелік тонів і перелік заливок —
   * ОДНЕ І ТЕ САМЕ. Прибери хтось рядок із `toneFills`, і цикли нижче просто
   * стали б коротшими: жоден із них не впав би, бо вони ітерують те, що є.
   */
  it('кожен тон має заливку, і зайвих заливок немає', () => {
    expect(Object.keys(toneFills).sort()).toEqual([...statusTones].sort());
    expect(statusTones).toHaveLength(5);
  });

  it.each(statusTones)('тон «%s» має розв’язний колір в обох схемах', (tone: StatusTone) => {
    const fill = fillOf(tone);

    for (const scheme of schemes) {
      expect(tokenValue(fill.text, scheme)).toMatch(/^#[0-9a-f]{6}$/i);
      expect(tokenValue(fill.bg, scheme)).toMatch(/^#[0-9a-f]{6}$/i);
    }
  });
});

describe('контраст тонів StatusBadge (ФВ-14.17)', () => {
  it.each(statusTones)('«%s»: напис читається на власній заливці — обидві схеми', (tone) => {
    const fill = fillOf(tone);

    for (const scheme of schemes) {
      const text = tokenValue(fill.text, scheme);
      const bg = tokenValue(fill.bg, scheme);

      expect(contrast(text, bg), `${tone}/${scheme}`).toBeGreaterThanOrEqual(AA.text);
    }
  });

  /*
   * ⚠ `quiet` (щільні таблиці) прибирає заливку — і тоді фоном стає САМА
   * СТОРІНКА. Це інша пара, ніж вище, і перевіряти лише заливку означало б
   * не перевірити половину випадків використання: `KIT.md` §6.7 ставить
   * `quiet` саме туди, де бейджів найбільше.
   */
  it.each(statusTones)('«%s»: quiet-напис читається на обох поверхнях обох схем', (tone) => {
    const fill = fillOf(tone);

    for (const scheme of schemes) {
      const text = tokenValue(fill.text, scheme);

      for (const surface of surfacesOf(scheme)) {
        expect(contrast(text, surface), `${tone}/${scheme}/${surface}`).toBeGreaterThanOrEqual(
          AA.text,
        );
      }
    }
  });
});

describe('перевірка не може бути зеленою випадково', () => {
  /*
   * ⚠ Калібрування лінійки — як у `theme/__tests__/contrast.test.ts`: без
   * нього функція, що завжди повертає 21, зробила б зеленим геть усе вище.
   */
  it('лінійка дає відомі значення', () => {
    expect(contrast('#000000', '#ffffff')).toBeCloseTo(21, 5);
    expect(contrast('#777777', '#ffffff')).toBeCloseTo(4.48, 2);
  });

  /*
   * ⛔ Другий бік мутації: доказ, що поріг тут справді щось відсіває. Саме
   * так виглядав би «простий» спосіб намалювати нейтральний і бляклий тони —
   * `variant="light" color="gray"` Mantine. `primaryShade: { light: 6 }`
   * бере `gray[6]` (`#868e96`) і кладе його на власну 10 %-заливку: ≈2.9:1,
   * тобто провал AA. Це та сама вада, що `W4.2` (`red`/`orange`) і `Q-262`
   * (`green`), просто на сірому — і саме тому тони беруть `--ecr-*`, а не
   * шкалу Mantine.
   */
  it('відкинутий варіант — gray Mantine — поріг НЕ проходить', () => {
    const gray = DEFAULT_THEME.colors['gray'];
    if (gray === undefined) throw new Error('у Mantine зникла шкала gray — перевірка втратила сенс');

    const grayShade = gray[6];
    if (grayShade === undefined) throw new Error('у шкалі gray немає відтінку 6');

    // `light`-варіант Mantine: 10 % кольору поверх тіла сторінки, текст — той
    // самий відтінок. Композит рахує наявний `flatten` із `theme/contrast.ts`.
    const composite = flatten(`${grayShade}1a`, tokenValue('var(--ecr-surface)', 'light'));

    expect(contrast(grayShade, composite)).toBeLessThan(AA.text);
  });

  it('токен, якого немає, валить перевірку з НАЗВАНОЮ причиною, а не мовчки', () => {
    expect(() => tokenValue('var(--ecr-no-such-token)', 'light')).toThrow(
      /не оголошений у cssVariablesResolver/,
    );

    expect(() => tokenValue('#c92a2a', 'light')).toThrow(/не на токен теми/);
    expect(() => fillOf('nope' as StatusTone)).toThrow(/колір ЗНИК/);
  });
});
