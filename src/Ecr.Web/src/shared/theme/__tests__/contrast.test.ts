import { readFileSync } from 'node:fs';
import path from 'node:path';
import { DEFAULT_THEME, defaultCssVariablesResolver, mergeMantineTheme } from '@mantine/core';
import { describe, it, expect } from 'vitest';
import { AA, contrast, flatten, parseColor } from '../contrast';
import { cssVariablesResolver } from '../cssVariables';
import {
  brand,
  cellState,
  statusError,
  statusSuccess,
  statusWarning,
  surfaces,
  theme,
  themeSurface,
} from '../theme';

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
   * ФВ-14.17: напис на заливці основної кнопки читається в обох темах.
   *
   * ⛔ Цієї перевірки НЕ БУЛО, і це виявилось при заміні плейсхолдерної
   * палітри на фірмову (2026-09-17). `primaryShade: { light: 6, dark: 5 }`
   * означає, що `variant="filled"` — тобто кожна кнопка «Зберегти»,
   * «Опублікувати», «Подати» — кладе БІЛИЙ напис на `brand[6]` у світлій темі
   * і на `brand[5]` у темній. Жоден тест цієї пари не міряв: гейт вище
   * стереже лише кільце фокуса, а воно має нижчий поріг (`nonText`, 3:1) і
   * бере ІНШІ індекси (6 і 4).
   *
   * ⚠ Наслідок був би тихий: фірмовий колір середньої світлоти дав би напису
   * ~3:1 — кнопка виглядала б нормально на знімку й була б нечитною під кутом
   * чи на поганому екрані, і жодна перевірка не впала б. Саме той клас
   * дефекту, через який `cellState` уже має власні виміряні токени.
   */
  it('ФВ-14.17: напис на основній кнопці читається в обох темах', () => {
    const label = '#ffffff';

    expect(contrast(label, brand[6])).toBeGreaterThanOrEqual(AA.text);
    expect(contrast(label, brand[5])).toBeGreaterThanOrEqual(AA.text);
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
      // ✎ 2026-09-22: `cssVariables.ts` (`statusOutline`) перевизначає темний
      // `outline` на `[3]` — `[1]` був майже білим, без статусного відтінку.
      // Ця перевірка кортежу лишена як є; що бачить браузер, стереже
      // `describe('статусні кольори: «outline» …')` нижче.
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

  /**
   * `Q-285`: той самий дефект (T7-02/T7-03), ще чотири виклики, лишені поза
   * межею `Q-262`/`Q-272` — `badgeColor()` у `HealthPage.tsx` (стан
   * `Healthy`), `stateColor()` у `JobsPage.tsx` (стан `Succeeded`) і
   * `PeriodsPage.tsx` (стан `Open`), і `showDone()` у `notify.ts` — СПІЛЬНИЙ
   * тост успіху, що показується з кожного екрана застосунку (роль створена,
   * користувач створений, доступи збережено...). Метод — той самий: перевірка
   * джерела (регрес не зникне непоміченим) плюс перевірка, що заміна дає
   * реальне покращення контрасту, не косметичну зміну імені.
   *
   * ✎ 2026-09-19. Трьох названих вище помічників БІЛЬШЕ НЕМАЄ: сторінки
   * перейшли на `StatusBadge`. Опис лишено як історію картки, а не як опис
   * чинного коду — шукати `badgeColor`/`stateColor` у цих файлах марно. Що
   * саме перевіряється натомість — у коментарі всередині `describe`.
   */
  describe('Q-285: чотири виклики поза Q-262/Q-272 — `statusSuccess`, не голий `green`', () => {
    /*
     * ✎ 2026-09-19, те саме перецілення, що й у `Q-289` нижче, і з тієї самої
     * причини. Тут стояли три дослівні `toMatch` на ТІЛА трьох локальних
     * помічників:
     *   `/if \(status === 'Healthy'\) return 'statusSuccess';/`     (HealthPage)
     *   `/case 'Succeeded':\s*\n\s*return 'statusSuccess';/`        (JobsPage)
     *   `/case 'Open':\s*\n\s*return 'statusSuccess';/`             (PeriodsPage)
     *
     * ⛔ Предметом картки був колір проти голого `green`, і в цьому сторож був
     * правий. Але, закріпивши РЕДАКЦІЮ, він заразом закріпив і саме існування
     * трьох окремих рішень про колір статусу — тобто ту розбіжність, яку
     * `StatusBadge` і заведено прибрати: `JobsPage.stateColor` віддавала
     * невідомому стану синій (той самий, що й `Queued`), `PeriodsPage
     * .stateColor` тим же синім фарбувала `Scheduled`, а `HealthPage
     * .badgeColor` мала власну трійку. Будь-яке зведення їх до набору
     * виглядало б для цього сторожа як регрес.
     *
     * ⚠ Тепер перевіряється ВЛАСТИВІСТЬ: жодна з трьох сторінок не має
     * власного рішення про колір статусу — він приходить із набору. Контраст
     * тонів набору стереже `shared/ui/__tests__/statusBadgeContrast.test.ts`,
     * розподіл станів за тонами — `StatusBadge.test.tsx` і три тести
     * `*.statusTone.test.tsx` поруч зі сторінками (рендером, не текстом).
     *
     * ⚠ Половина `Q-285`, що рахує контраст токенів, і рядок про
     * `notify.showDone()` лишилися незмінними: вони не читають цих сторінок.
     */
    const kitPages: readonly (readonly [string, string, string])[] = [
      ['HealthPage', 'src/pages/admin/HealthPage.tsx', 'health'],
      ['JobsPage', 'src/pages/admin/JobsPage.tsx', 'job'],
      ['PeriodsPage', 'src/pages/admin/PeriodsPage.tsx', 'period'],
    ];

    it.each(kitPages)('%s: статус іде через набір, а не через власного помічника', (_name, file, kind) => {
      const source = readFileSync(path.resolve(process.cwd(), file), 'utf8');

      expect(source).toMatch(new RegExp(`<StatusBadge kind="${kind}" state=\\{`));

      /*
       * ⛔ Мутаційний доказ на джерело в обидва боки: і повернення локального
       * помічника, і голий `green` замість токена ловляться тут-таки.
       *
       * ⚠ Саме ОГОЛОШЕННЯ функції, а не будь-яка згадка імені: коментар поруч
       * із виправленням цитує стару назву, і сторож «на згадку» впав би на
       * власному поясненні — рівно так, як це вже сталося з `Q-289`.
       */
      expect(source).not.toMatch(/function (badgeColor|stateColor)\s*\(/);
      expect(source).not.toMatch(/color=\{(badgeColor|stateColor)\(/);
      expect(source).not.toMatch(/color="green"|color=\{'green'\}/);
    });

    /**
     * ⛔ Найвищий важіль цієї картки: `showDone()` — ОДНА функція, викликана з
     * кожного тосту успіху в застосунку (роль створена, користувач створений,
     * доступи збережено, період відкрито/зафіксовано...). Виправлення тут
     * діє на ВСІ ці місця одночасно, без правки кожного окремо.
     */
    it('notify.showDone(): спільний тост успіху — `statusSuccess`', () => {
      const source = readFileSync(
        path.resolve(process.cwd(), 'src/shared/ui/notify.ts'),
        'utf8',
      );

      // ⛔ Перевіряється ТІЛО `showDone`, а не дослівний однорядковий виклик.
      // Тут стояло `toMatch(/notifications\.show\(\{ color: 'statusSuccess',
      // message \}\);/)` — і воно впало не на регресі кольору, а на тому, що
      // виклик став багаторядковим (до нього додали `closeButtonProps`, F8).
      // Сторож, прив'язаний до форматування, оголошує дефектом переніс рядка:
      // предмет картки — колір, і саме його треба тримати.
      const body = source.slice(source.indexOf('export function showDone'));

      expect(body).toMatch(/color: 'statusSuccess'/);
      expect(body).not.toMatch(/color: 'green'/);
    });

    // Той самий `blend`, що й для Q-272 вище: доводить не лише присутність
    // рядка, а й що підміна дає РЕАЛЬНЕ покращення контрасту — усі чотири
    // виклики використовують `filled` (Badge за замовчуванням, Notification),
    // не `light`, тому композит — прямий текст-на-заливці, без alpha-змішування.
    it('усі чотири виклики: `statusSuccess` filled проходить AA там, де голий `green` провалювався', () => {
      const defaultMantineGreen5 = '#51cf66';
      const defaultMantineGreen6 = '#40c057';

      // ДО (контроль регресу): саме ці два виміряні контрасти документує
      // `theme.ts` (2.36:1 світла / 2.01:1 темна) — обидва глибоко нижче AA.
      expect(contrast('#ffffff', defaultMantineGreen5)).toBeLessThan(AA.text);
      expect(contrast('#ffffff', defaultMantineGreen6)).toBeLessThan(AA.text);

      // ПІСЛЯ: `statusSuccess` на тих самих індексах (`filled`, обидві теми).
      expect(contrast('#ffffff', statusSuccess[6])).toBeGreaterThanOrEqual(AA.text);
      expect(contrast('#ffffff', statusSuccess[5])).toBeGreaterThanOrEqual(AA.text);
    });
  });

  /**
   * `Q-289`: той самий дефект (T7-02/T7-03), ще один виклик, лишений поза
   * межею `Q-285` (яка сама зафіксувала себе як «чотири виклики» —
   * `HealthPage`, `JobsPage`, `PeriodsPage`, `notify.showDone()`; `Badge`
   * у `SourcesPage.tsx` («останній запуск», стан `Succeeded`) до переліку
   * не увійшов). `variant="light"` — той самий резолвер кольору, що й
   * `Q-272` (alpha-композит, не пряма заливка), тож перевірка контрасту
   * тут дзеркалить `Q-272`, а не `Q-285`.
   */
  describe('Q-289: SourcesPage — «останній запуск» не фарбується вручну', () => {
    /*
     * ✎ 2026-09-19. Тут стояло дослівне
     * `toMatch(/source\.lastRun\.status === 'Succeeded' \? 'statusSuccess' : 'statusWarning'/)`
     * — тобто сторож ВИМАГАВ саме ту тернарку, яка й була дефектом: вона
     * фарбувала `Failed` (даних немає зовсім) тим самим `statusWarning`, що й
     * `Degraded` (є частина). Предметом картки був колір проти голого `green`,
     * і в цій частині сторож був правий; але, закріпивши ВИРАЗ, він заразом
     * закріпив і хибний розподіл станів, і будь-яке виправлення виглядало б
     * як регрес.
     *
     * ⛔ Урок ширший за цей рядок: сторож по тексту джерела охороняє не
     * властивість, а редакцію. Тепер перевіряється властивість — сторінка не
     * має ВЛАСНОГО рішення про колір статусу прогону; він приходить із набору
     * (`StatusBadge`, різновид `collectionRun`), а тони набору стереже
     * `shared/ui/__tests__/statusBadgeContrast.test.ts`. Розподіл станів за
     * тонами доводить `SourcesPage.runStatusTone.test.tsx` — рендером, не
     * текстом.
     */
    it('SourcesPage: статус прогону йде через набір, а не через власну тернарку', () => {
      const source = readFileSync(
        path.resolve(process.cwd(), 'src/pages/admin/SourcesPage.tsx'),
        'utf8',
      );

      expect(source).toMatch(/<StatusBadge kind="collectionRun" state=\{source\.lastRun\.status\}/);

      // ⚠ Саме проп `color`, обчислений із `lastRun`, а не будь-яка згадка
      // стану: коментар поруч із виправленням цитує стару тернарку, і сторож
      // на «згадку» впав би на власному поясненні.
      expect(source).not.toMatch(/color=\{[^}]*lastRun/);
      expect(source).not.toMatch(/color="green"|color=\{'green'\}/);
    });

    // Той самий `blend`, що й для Q-272: доводить не лише присутність рядка,
    // а й що підміна дає РЕАЛЬНЕ покращення контрасту — Badge тут теж
    // `variant="light"`, тобто alpha-композит, не пряма заливка.
    it('підміна дає реальне покращення контрасту (variant="light", той самий композит, що й Q-272)', () => {
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
      // текст і тло `light`-варіанта у світлій темі. Провал AA.
      const defaultMantineGreen6 = '#40c057';
      const beforeComposite = blend(defaultMantineGreen6, 0.1, '#ffffff');
      expect(contrast(defaultMantineGreen6, beforeComposite)).toBeLessThan(AA.text);

      // ПІСЛЯ: `statusSuccess[6]` — той самий композит, проходить AA.
      const afterComposite = blend(statusSuccess[6], 0.1, '#ffffff');
      expect(contrast(statusSuccess[6], afterComposite)).toBeGreaterThanOrEqual(AA.text);
    });
  });

  it('обчислення контрасту дає відомі значення', () => {
    // ⚠ Калібрування самої лінійки. Без нього тест перевіряв би власну
    // помилку: функція, що завжди повертає 21, зробила б усе вище зеленим.
    expect(contrast('#000000', '#ffffff')).toBeCloseTo(21, 5);
    expect(contrast('#777777', '#ffffff')).toBeCloseTo(4.48, 2);
    expect(contrast('#ffffff', '#ffffff')).toBeCloseTo(1, 5);
  });
});

/**
 * Текст `variant="subtle"`/`"light"` статусних кольорів — саме статусний, і
 * читається на кожній поверхні сторінки.
 *
 * ⛔ Дефект, якого не бачив жоден поріг: у темній схемі Mantine бере текст
 * `--mantine-color-<status>-light-color` за індексом
 * `max(primaryShade.dark − 5, 0)` = `[0]` — майже білий (`#fff5f5` для помилки).
 * Контраст такого тексту 9–17:1, тобто AA він проходить із запасом; зламано не
 * читабельність, а сам зміст — кнопка «Видалити» виглядала як звичайний текст.
 * Тому поруч із порогом стоїть вимога відтінку: текст мусить дорівнювати
 * статусному тексту схеми (`--ecr-danger`/`-warning`/`-success`) і НЕ бути `[0]`.
 *
 * ⚠ Змінні беруться не з кортежу, а з того, що побачить браузер: вивід
 * дефолтного резолвера Mantine, поверх нього — наш `cssVariablesResolver`
 * (той самий порядок, що `getMergedVariables` у `@mantine/core`), з
 * розгорнутими `var(...)`. Інакше тест перевіряв би індекс, який ми ВВАЖАЄМО
 * взятим, а не той, що береться насправді.
 */
describe('статусні кольори: текст «subtle»/«light» — статусного відтінку', () => {
  type Scheme = 'light' | 'dark';

  /** Змінні схеми так, як їх злиє Mantine: дефолт, поверх — наш резолвер. */
  function mergedVars(scheme: Scheme): Record<string, string> {
    const full = mergeMantineTheme(DEFAULT_THEME, theme);
    const base = defaultCssVariablesResolver(full);
    const ours = cssVariablesResolver(full);

    return { ...base.variables, ...base[scheme], ...ours.variables, ...ours[scheme] };
  }

  /** Значення змінної з розгорнутими `var(--x)`; відсутня — падіння. */
  function deref(vars: Record<string, string>, name: string): string {
    let value = vars[name];

    for (let depth = 0; depth < 10 && value !== undefined; depth++) {
      const ref = /^var\((--[\w-]+)\)$/.exec(value.trim());
      if (ref === null) return value.trim().toLowerCase();
      value = vars[ref[1]!];
    }

    throw new Error(`змінна «${name}» не розгортається в колір`);
  }

  const statuses = [
    ['statusError', statusError, '--ecr-danger'],
    ['statusWarning', statusWarning, '--ecr-warning'],
    ['statusSuccess', statusSuccess, '--ecr-success'],
  ] as const;

  const cases = statuses.flatMap(([name, tuple, ecr]) =>
    (['light', 'dark'] as const).map((scheme) => [name, scheme, tuple, ecr] as const),
  );

  /** Поверхні-«сторінки», на яких стоїть кнопка. */
  const pages = ['ground', 'surface', 'sunken', 'raised'] as const;

  it.each(cases)('«%s», схема «%s»: текст — статусний відтінок, не `[0]`', (name, scheme, tuple, ecr) => {
    const vars = mergedVars(scheme);
    const text = deref(vars, `--mantine-color-${name}-light-color`);

    expect(text, `${scheme}: ${name} light-color`).not.toBe(tuple[0].toLowerCase());
    expect(text, `${scheme}: ${name} light-color ≠ ${ecr}`).toBe(deref(vars, ecr));
  });

  it.each(cases)('«%s», схема «%s»: «subtle» читається на кожній поверхні', (name, scheme) => {
    const vars = mergedVars(scheme);
    const text = deref(vars, `--mantine-color-${name}-light-color`);

    // `subtle` — прозоре тло, тобто текст лежить прямо на поверхні.
    for (const page of pages) {
      const bg = surfaces[scheme][page];

      expect(contrast(text, bg), `${scheme}: ${name} на ${page}`).toBeGreaterThanOrEqual(AA.text);
    }
  });

  /*
   * ⚠ Композит `-light` (тло `variant="light"`) і `-light-hover` (тло наведення
   * `subtle`) — лише темна схема. У світлій той самий композит на `sunken`
   * (`#eef0f5`) дає помилці 4.11/3.99 і успіху 4.50/4.36 — це інший дефект
   * (тло, а не текст; індекс тексту там правильний), і він названий у звіті
   * PR, а не закритий тут мовчки.
   *
   * ✎ 2026-09-22: закрито — `statusLightBackground` у `cssVariables.ts`;
   * перевірка світлої схеми — у тесті одразу нижче.
   */
  it.each(statuses)('«%s», темна схема: «light»/hover-тло не зʼїдає текст', (name) => {
    const vars = mergedVars('dark');
    const text = deref(vars, `--mantine-color-${name}-light-color`);

    for (const page of pages) {
      const bg = surfaces.dark[page];

      for (const layer of ['light', 'light-hover'] as const) {
        const tint = flatten(deref(vars, `--mantine-color-${name}-${layer}`), bg);

        expect(contrast(text, tint), `dark: ${name} на ${layer} поверх ${page}`).toBeGreaterThanOrEqual(
          AA.text,
        );
      }
    }
  });

  /*
   * Світла схема: тло `light` і наведення `subtle` — текст `[6]` читається на
   * кожній поверхні, ВКЛЮЧНО з `sunken`, а тло лишається статусним: не
   * прозоре-майже-ніщо і не сіре. «Статусне» тут вимірюється так: у тла,
   * покладеного на поверхню, переважає той самий канал, що в статусного `[6]`
   * (R — помилка/попередження, G — успіх), і з запасом ≥ 8 одиниць. Поверхні
   * самі синюваті (`#eef0f5`: переважає B), тож сіре тло цю перевірку провалює.
   */
  function dominant(hex: string): string {
    const { r, g, b } = parseColor(hex);
    const sorted = [
      ['r', r],
      ['g', g],
      ['b', b],
    ] as const;
    const [top, second] = [...sorted].sort((x, y) => y[1] - x[1]);

    return top![1] - second![1] >= 8 ? top![0] : 'сіре';
  }

  it.each(statuses)('«%s», світла схема: «light»/hover-тло не зʼїдає текст `[6]`', (name, tuple) => {
    const vars = mergedVars('light');
    const text = deref(vars, `--mantine-color-${name}-light-color`);

    expect(text).toBe(tuple[6].toLowerCase());

    for (const page of pages) {
      const bg = surfaces.light[page];

      for (const layer of ['light', 'light-hover'] as const) {
        const tint = flatten(deref(vars, `--mantine-color-${name}-${layer}`), bg);

        expect(contrast(text, tint), `light: ${name} на ${layer} поверх ${page}`).toBeGreaterThanOrEqual(
          AA.text,
        );
        expect(tint, `light: ${name} ${layer} поверх ${page} — не сама поверхня`).not.toBe(bg);
        expect(dominant(tint), `light: ${name} ${layer} поверх ${page} — тло статусне, не сіре`).toBe(
          dominant(tuple[6]),
        );
      }
    }
  });

  /*
   * `variant="outline"`: текст і межа — `--mantine-color-<c>-outline`, тло
   * наведення — `-outline-hover` (`getCSSColorVariables`). У темній схемі
   * Mantine бере `[1]` — майже білий; мусить бути статусний `--ecr-*`.
   */
  it.each(cases)('«%s», схема «%s»: «outline» — статусний відтінок, AA на кожній поверхні', (name, scheme, tuple, ecr) => {
    const vars = mergedVars(scheme);
    const outline = deref(vars, `--mantine-color-${name}-outline`);

    expect(outline, `${scheme}: ${name} outline`).not.toBe(tuple[0].toLowerCase());
    expect(outline, `${scheme}: ${name} outline`).not.toBe(tuple[1].toLowerCase());
    expect(outline, `${scheme}: ${name} outline ≠ ${ecr}`).toBe(deref(vars, ecr));

    // Темна: наведення — той самий відтінок, що межа (Mantine рахує його з
    // того самого індексу). Світла: непрозоре `[0]` — `statusLightBackground`.
    const hover = parseColor(deref(vars, `--mantine-color-${name}-outline-hover`));
    const base = parseColor(scheme === 'dark' ? outline : tuple[0]);
    expect([hover.r, hover.g, hover.b], `${scheme}: ${name} outline-hover`).toEqual([base.r, base.g, base.b]);

    for (const page of pages) {
      const bg = surfaces[scheme][page];
      const hovered = flatten(deref(vars, `--mantine-color-${name}-outline-hover`), bg);

      expect(contrast(outline, bg), `${scheme}: ${name} outline на ${page}`).toBeGreaterThanOrEqual(AA.text);
      expect(contrast(outline, hovered), `${scheme}: ${name} outline на hover поверх ${page}`).toBeGreaterThanOrEqual(
        AA.text,
      );
    }
  });

  it('лінійка: `deref` падає на відсутній змінній, а не повертає `undefined`', () => {
    expect(() => deref({}, '--mantine-color-statusError-light-color')).toThrow(/не розгортається/);
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

  /**
   * ⛔ Поверхня й текст сітки продубльовані в CSS із тієї самої причини, що й
   * заливки станів: CSS не читає TypeScript. Але ціна розходження тут вища —
   * саме цими змінними перебивається світлий за замовчуванням CSS RevoGrid
   * (`cell-states.css`, блок «RevoGrid: світлі значення пакета проти темної
   * теми»). Розійдись вони з `themeSurface`, і перевірка контрасту комірки
   * рахувала б контраст проти фону, якого на екрані немає, — тобто рівно той
   * дефект, який вона й має ловити.
   */
  it.each(['light', 'dark'] as const)(
    'ФВ-14.11: поверхня і текст сітки збігаються з `themeSurface` (%s)',
    (scheme) => {
      expect(cssVar('--ecr-grid-surface', scheme)).toBe(themeSurface[scheme].body);
      expect(cssVar('--ecr-grid-text', scheme)).toBe(themeSurface[scheme].text);
    },
  );

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
