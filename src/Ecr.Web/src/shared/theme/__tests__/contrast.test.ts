import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, it, expect } from 'vitest';
import { AA, contrast } from '../contrast';
import { brand, cellState, statusError, statusSuccess, statusWarning, themeSurface } from '../theme';

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

  it('ФВ-14.15: кільце фокуса контрастне в обох темах', () => {
    // `brand[6]` у світлій, `brand[4]` у темній — так задано в `motion.css`
    // (`--mantine-color-brand-6` / `--mantine-color-brand-4`). Береться з
    // `theme.ts`, а не літералом: інакше заміна плейсхолдерної палітри на
    // фірмову NCOC не зрушила б цей тест ні на йоту, і він мовчки перевіряв
    // би контраст кольору, якого вже немає в застосунку.
    expect(contrast(brand[6], themeSurface.light.body)).toBeGreaterThanOrEqual(AA.nonText);
    expect(contrast(brand[4], themeSurface.dark.body)).toBeGreaterThanOrEqual(AA.nonText);
  });

  /**
   * W4.2: статусні кольори (`error`/`warning`) замінили голі `color="red"` /
   * `color="orange"` у фічах — і ось чому голе ім'я було дефектом, а не
   * стилем. `primaryShade: { light: 6, dark: 5 }` застосовується до
   * КОЖНОГО кольору Mantine, не лише до `brand` (перевірено читанням
   * `getPrimaryShade` у `@mantine/core`): відтінок `filled`-варіанта в
   * темній темі береться за індексом 5. Стандартна Mantine-шкала `red`/
   * `orange` на цьому індексі світла, і білий текст на ній давав ~2.2–2.8:1
   * — під AA. Жоден із двох контрольних кольорів не був наведений на око:
   * значення підібрані перебором під REAL-варіанти Mantine (`filled` —
   * біла мітка на заливці; `outline` — текст/межа на тлі сторінки), в обох
   * схемах.
   *
   * `Q-262` додав третій: `success` — те саме, знайдене на `<Badge
   * color="green">` (2.36:1 світла / 2.01:1 темна) і `<Alert color="green"
   * variant="light">` (2.36:1 текст на власному тлі, світла тема).
   */
  describe.each([
    ['error', statusError],
    ['warning', statusWarning],
    ['success', statusSuccess],
  ] as const)('статусний колір «%s» контрастний у варіантах Mantine', (name, tuple) => {
    it(`«filled» (біла мітка на заливці) — обидві схеми (${name})`, () => {
      // Індекс 6 — заливка `filled` у СВІТЛІЙ схемі (`primaryShade.light`),
      // індекс 5 — та сама заливка в ТЕМНІЙ (`primaryShade.dark`). Саме
      // індекс 5, а не 8 (Mantine-дефолт для типової теми), і саме тут
      // стандартна `red`/`orange`/`green` провалювались.
      expect(contrast('#ffffff', tuple[6])).toBeGreaterThanOrEqual(AA.text);
      expect(contrast('#ffffff', tuple[5])).toBeGreaterThanOrEqual(AA.text);
    });

    it(`«outline»/«light» (текст на тлі сторінки) — обидві схеми (${name})`, () => {
      // Світла схема: текст/межа `outline` і текст `light` — той самий
      // індекс 6. Темна схема: `outline` бере індекс 1
      // (`Math.max(primaryShade.dark - 4, 0)` = 1) — БЛІДИЙ відтінок навмисно:
      // на темному тлі яскравий/темний відтінок або зникає, або ріже очі.
      expect(contrast(tuple[6], themeSurface.light.body)).toBeGreaterThanOrEqual(AA.nonText);
      expect(contrast(tuple[1], themeSurface.dark.body)).toBeGreaterThanOrEqual(AA.nonText);
    });
  });

  /**
   * `Q-262`: перевірка саме `<Alert variant="light">`-тексту (не лише
   * `outline`), бо реальний фон — не суцільна сторінка, а власна 10%/15%-
   * заливка кольору поверх неї (`getCSSColorVariables` у `@mantine/core`).
   * Наближення до суцільного тла в тесті вище достатнє для error/warning,
   * але саме тут і був живий дефект T7-03 — тому рахуємо композит явно.
   */
  it('«light» (Alert): текст «success» читається на власному тлі, обидві схеми', () => {
    // Композитує `hex` з непрозорістю `alphaValue` поверх `bgHex` — те саме
    // рівняння, що й `alpha()`/`rgba()` у `@mantine/core`.
    function blend(hex: string, alphaValue: number, bgHex: string): string {
      const parse = (h: string): [number, number, number] => {
        const m = h.match(/^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i);
        if (m === null) throw new Error(`не hex-колір: ${h}`);
        return [parseInt(m[1]!, 16), parseInt(m[2]!, 16), parseInt(m[3]!, 16)];
      };
      const [fr, fg, fb] = parse(hex);
      const [br, bg, bb] = parse(bgHex);
      const mix = (f: number, b: number) => Math.round(f * alphaValue + b * (1 - alphaValue));
      const toHex = (v: number) => v.toString(16).padStart(2, '0');
      return `#${toHex(mix(fr, br))}${toHex(mix(fg, bg))}${toHex(mix(fb, bb))}`;
    }

    // Світла тема: тло `light`-варіанта — alpha(tuple[6], 0.1) поверх білого
    // тіла сторінки; текст — tuple[6] (той самий індекс, що й filled).
    const composedLight = blend(statusSuccess[6], 0.1, '#ffffff');
    expect(contrast(statusSuccess[6], composedLight)).toBeGreaterThanOrEqual(AA.text);

    // Темна тема: тло — alpha(tuple[3], 0.15) поверх тіла `#242424`; текст —
    // tuple[Math.max(primaryShade.dark - 5, 0)] = tuple[0] (максимально
    // блідий відтінок, навмисно — щоб не зникнути й не сліпити на темному).
    const composedDark = blend(statusSuccess[3], 0.15, themeSurface.dark.body);
    expect(contrast(statusSuccess[0], composedDark)).toBeGreaterThanOrEqual(AA.text);
  });

  /**
   * `Q-262`, мутаційний доказ: якщо `statusSuccess[5]`/`[6]` відкотити до
   * будь-якого відтінку дефолтної Mantine-шкали `green` (де б не лежав
   * поріг AA — на 5, 6 чи будь-де іншому), тест НИЖЧЕ мусить впасти. Просто
   * «контраст ≥ 4.5» не ловить регрес, де хтось поверне «зелений, який
   * виглядає як зелений» замість підібраного — тому тут перевіряється
   * КОНКРЕТНЕ значення, а не сам факт проходження порогу.
   */
  it('Q-262: «success» — саме підібраний відтінок, не дефолтний Mantine `green`', () => {
    expect(statusSuccess[5]).toBe('#1a7431');
    expect(statusSuccess[6]).toBe('#1a7431');

    // Дефолтна Mantine-шкала `green` на цих самих індексах — контроль, що
    // цей тест справді ловить регрес: без override цей контраст провалюється.
    const defaultMantineGreen5 = '#51cf66';
    const defaultMantineGreen6 = '#40c057';
    expect(contrast('#ffffff', defaultMantineGreen5)).toBeLessThan(AA.text);
    expect(contrast('#ffffff', defaultMantineGreen6)).toBeLessThan(AA.text);
  });

  /**
   * `Q-272`: той самий дефект (T7-02/T7-03), лишений ПОЗА межею Q-262 —
   * `<Badge color="green" variant="light">` («збіглося»/«ні») у
   * `AccessDiagnosticsPanel.tsx`, рядок 83. Q-262 сам це зафіксував як
   * НЕзаймане: «інший виклик, поза переліком файлів картки». Композит той
   * самий, що й для `Alert` вище (`getCSSColorVariables` у `@mantine/core`
   * — `variant="light"` без явного відтінку бере текст і тло з
   * `tuple[primaryShade]` ОДНИМ резолвером для будь-якого компонента, не
   * лише `Alert`), тож дефект і виправлення ідентичні.
   */
  it('Q-272: Badge «збіглося» в AccessDiagnosticsPanel — `statusSuccess`, не голий `green`', () => {
    const source = readFileSync(
      path.resolve(process.cwd(), 'src/features/security/AccessDiagnosticsPanel.tsx'),
      'utf8',
    );

    // ⛔ Мутаційний доказ на джерело: поверни хтось голий `'green'` замість
    // `'statusSuccess'` — цей рядок ловить регрес одразу, не чекаючи, доки
    // хтось відкриє DevTools і виміряє контраст вручну.
    expect(source).toMatch(/color=\{group\.matched \? 'statusSuccess' : 'gray'\}/);
    expect(source).not.toMatch(/color=\{group\.matched \? 'green' : 'gray'\}/);

    // Той самий `blend`, що й для Alert вище: перевіряє не лише присутність
    // рядка, а й що підміна дає РЕАЛЬНЕ покращення контрасту, не косметичну
    // зміну імені.
    function blend(hex: string, alphaValue: number, bgHex: string): string {
      const parse = (h: string): [number, number, number] => {
        const m = h.match(/^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i);
        if (m === null) throw new Error(`не hex-колір: ${h}`);
        return [parseInt(m[1]!, 16), parseInt(m[2]!, 16), parseInt(m[3]!, 16)];
      };
      const [fr, fg, fb] = parse(hex);
      const [br, bg, bb] = parse(bgHex);
      const mix = (f: number, b: number) => Math.round(f * alphaValue + b * (1 - alphaValue));
      const toHex = (v: number) => v.toString(16).padStart(2, '0');
      return `#${toHex(mix(fr, br))}${toHex(mix(fg, bg))}${toHex(mix(fb, bb))}`;
    }

    // ДО (контроль регресу): дефолтна Mantine-шкала `green`, індекс 6 —
    // текст і тло `light`-варіанта у світлій темі. 2.175:1 — провал AA.
    const defaultMantineGreen6 = '#40c057';
    const beforeComposite = blend(defaultMantineGreen6, 0.1, '#ffffff');
    expect(contrast(defaultMantineGreen6, beforeComposite)).toBeLessThan(AA.text);

    // ПІСЛЯ: `statusSuccess[6]` — той самий композит, 5.075:1 — проходить.
    const afterComposite = blend(statusSuccess[6], 0.1, '#ffffff');
    expect(contrast(statusSuccess[6], afterComposite)).toBeGreaterThanOrEqual(AA.text);
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
  it.each(names)('ФВ-14.11: значення стану «%s» однакові у двох джерелах', (name) => {
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
