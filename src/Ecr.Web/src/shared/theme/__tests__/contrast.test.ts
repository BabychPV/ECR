import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, it, expect } from 'vitest';
import { AA, contrast } from '../contrast';
import { cellState, themeSurface } from '../theme';

// ⚠ Шлях від кореня проєкту, а не від import.meta.url: під jsdom
// він не має схеми file:, і fileURLToPath кидає виняток.
const css = readFileSync(
  path.resolve(process.cwd(), 'src/shared/theme/cell-states.css'),
  'utf8',
);

/** Значення змінної CSS у вказаному блоці. */
function cssVar(name: string, scheme: 'light' | 'dark'): string | null {
  // Світлі значення — у `:root`, темні — у блоці `[data-mantine-color-scheme='dark']`.
  const blocks = css.split(':root');
  const block = blocks.find((b) =>
    scheme === 'dark' ? b.includes("data-mantine-color-scheme='dark'") : b.trimStart().startsWith('{'),
  );

  return block?.match(new RegExp(`${name}:\\s*(#[0-9a-f]{3,8})`, 'i'))?.[1]?.toLowerCase() ?? null;
}

/** `readOnly` → `read-only`. */
function kebab(name: string): string {
  return name.replace(/[A-Z]/g, (letter) => `-${letter.toLowerCase()}`);
}

const names = Object.keys(cellState) as (keyof typeof cellState)[];

describe('Контраст токенів (ФВ-14.17)', () => {
  it.each(names)('текст читається в комірці «%s» в обох темах', (name) => {
    const token = cellState[name];

    // ⛔ Перевіряється КОЖНА тема окремо. Темна тема, зроблена інверсією
    // світлої, дає формально «ті самі» кольори і провалює контраст: `#fff8e1`
    // на темному фоні світиться, а не позначає.
    expect(contrast(token.light.bg, themeSurface.light.text)).toBeGreaterThanOrEqual(AA.text);
    expect(contrast(token.dark.bg, themeSurface.dark.text)).toBeGreaterThanOrEqual(AA.text);
  });

  it.each(names)('лінія стану «%s» видна на обох фонах, з якими межує', (name) => {
    const token = cellState[name];

    // ⛔ Лінія межує з ДВОМА фонами: своєю коміркою і сусідньою (тобто тлом
    // сторінки). Перевірка лише проти власного фону пропустила б первісний
    // `#f0b429`, який давав проти білого 1.8:1.
    expect(contrast(token.light.line, token.light.bg)).toBeGreaterThanOrEqual(AA.nonText);
    expect(contrast(token.light.line, themeSurface.light.body)).toBeGreaterThanOrEqual(AA.nonText);
    expect(contrast(token.dark.line, token.dark.bg)).toBeGreaterThanOrEqual(AA.nonText);
    expect(contrast(token.dark.line, themeSurface.dark.body)).toBeGreaterThanOrEqual(AA.nonText);
  });

  it('кільце фокуса контрастне в обох темах', () => {
    // `brand-6` у світлій, `brand-4` у темній — так задано в `motion.css`.
    expect(contrast('#5474b4', themeSurface.light.body)).toBeGreaterThanOrEqual(AA.nonText);
    expect(contrast('#748dc1', themeSurface.dark.body)).toBeGreaterThanOrEqual(AA.nonText);
  });

  it('обчислення контрасту дає відомі значення', () => {
    // ⚠ Калібрування самої лінійки. Без нього тест перевіряв би власну
    // помилку: функція, що завжди повертає 21, зробила б усе вище зеленим.
    expect(contrast('#000000', '#ffffff')).toBeCloseTo(21, 5);
    expect(contrast('#777777', '#ffffff')).toBeCloseTo(4.48, 2);
    expect(contrast('#ffffff', '#ffffff')).toBeCloseTo(1, 5);
  });
});

describe('theme.ts і cell-states.css не розходяться', () => {
  it.each(names)('значення стану «%s» однакові у двох джерелах', (name) => {
    const token = cellState[name];
    const slug = kebab(name);

    // ⛔ CSS не читає TypeScript, тому значення продубльовані. Дублювання без
    // звірки — це два джерела правди: колір, підправлений в одному місці,
    // мовчки розійшовся б з іншим, і тест контрасту перевіряв би не те, що
    // бачить користувач.
    expect(cssVar(`--ecr-cell-${slug}-bg`, 'light')).toBe(token.light.bg);
    expect(cssVar(`--ecr-cell-${slug}-line`, 'light')).toBe(token.light.line);
    expect(cssVar(`--ecr-cell-${slug}-bg`, 'dark')).toBe(token.dark.bg);
    expect(cssVar(`--ecr-cell-${slug}-line`, 'dark')).toBe(token.dark.line);
  });

  it('кожен стан має власний клас у CSS', () => {
    for (const name of names) {
      expect(css).toContain(`.ecr-cell--${kebab(name)}`);
    }
  });

  it('кожен стан несе другий носій, а не самий лише фон (ФВ-14.18)', () => {
    for (const name of names) {
      const rule = css.slice(css.indexOf(`.ecr-cell--${kebab(name)}`));
      const body = rule.slice(0, rule.indexOf('}'));
      const marker = css.includes(`.ecr-cell--${kebab(name)}::`);

      // ⛔ Або власна межа, або штрихування, або маркер у псевдоелементі.
      // Стан, у якого є лише `background`, порушує ФВ-14.18 — і саме так
      // виглядав `.ecr-cell-readonly` до цього етапу.
      const hasShape =
        body.includes('border-left') || body.includes('repeating-linear-gradient') || marker;

      expect(hasShape, `стан ${name} не має другого носія`).toBe(true);
    }
  });
});
