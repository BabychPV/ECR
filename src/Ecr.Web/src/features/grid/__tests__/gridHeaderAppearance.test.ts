import { describe, it, expect } from 'vitest';
import { AA, contrast } from '@/shared/theme/contrast';
import { surfaces, themeSurface } from '@/shared/theme/theme';
import { declared, gridCascade, type El } from './cssCascade';

/**
 * Шапка сітки документа читається як шапка (`U-04`).
 *
 * ⛔ Що саме було зламано. У браузері на живому документі `.rgHeaderCell` мала
 * `background-color: rgba(0, 0, 0, 0)` і `border-bottom: 0px none` — тобто від
 * рядка даних її відрізняв рівно `font-weight: 600` пакета. Для порівняння,
 * звичайні `<th>` цього ж застосунку (перелік документів, одиниці, шаблони) —
 * заливка плюс вага 700. Механізму при цьому не бракувало: заголовок ОБРАНОЇ
 * колонки пакет заливає сам (`.rgHeaderCell.focused-cell`), тобто бракувало
 * саме СТАНУ СПОКОЮ.
 *
 * ⛔ Доказ — обчислення над САМИМИ таблицями стилів, включно з двома, що
 * лежать у `node_modules`, тим самим резолвером каскаду, що вже доводить
 * контраст комірки (`gridCellContrast.test.ts`). Рендер тут не допоміг би:
 * jsdom не реалізує каскад за специфічністю для довільних селекторів, а саме
 * специфічність тут і вирішує — порядок вставки стилів Stencil відносно нашого
 * бандла не гарантований, тож наше правило мусить вигравати СТРОГО.
 */

const Schemes = ['light', 'dark'] as const;
type Scheme = (typeof Schemes)[number];

/**
 * Значення токена теми, який стоїть у правилі.
 *
 * ⛔ Не копія кольору поруч із тестом: ті самі поля `surfaces`, які
 * `cssVariables.ts` і видає під цими іменами. Розійтися ці два списки не
 * мають права — інакше тест міряв би колір, якого на екрані немає, рівно той
 * дефект, який `gridCellContrast.test.ts` уже одного разу спіймав.
 */
const TokenValue: Record<string, (scheme: Scheme) => string> = {
  '--ecr-sunken': (scheme) => surfaces[scheme].sunken,
  '--ecr-text': (scheme) => surfaces[scheme].text,
  '--ecr-border-strong': (scheme) => surfaces[scheme].borderStrong,
  '--ecr-accent-soft': (scheme) => surfaces[scheme].accentSoft,
};

/** `var(--ecr-x)` → колір теми; будь-що інше — помилка тесту, а не «нічого». */
function colorOf(value: string | null, scheme: Scheme): string {
  const token = /^var\(\s*(--[\w-]+)\s*\)$/.exec((value ?? '').trim())?.[1] ?? '';
  const resolve = TokenValue[token];

  expect(resolve, `оголошення «${String(value)}» — не токен теми`).toBeDefined();

  return (resolve as (s: Scheme) => string)(scheme);
}

/** Корінь документа: схему задає Mantine атрибутом на `<html>`. */
function rootOf(scheme: Scheme): El {
  return { tag: 'html', isRoot: true, attrs: { 'data-mantine-color-scheme': scheme } };
}

/**
 * Ланцюжок предків комірки шапки — рівно той, що будує RevoGrid
 * (`header-cell-renderer.js` усередині `revogr-header`).
 */
function headerChain(scheme: Scheme, focused: boolean): El[] {
  const classes = ['rgHeaderCell'];
  if (focused) classes.push('focused-cell');

  return [
    rootOf(scheme),
    { tag: 'revo-grid', attrs: { theme: 'compact' } },
    { tag: 'revogr-header' },
    { tag: 'div', classes: ['header-rgRow'] },
    { tag: 'div', classes },
  ];
}

describe('Шапка сітки документа (U-04)', () => {
  const rules = gridCascade();

  describe.each(Schemes)('схема «%s»', (scheme) => {
    it('у спокої має заливку, а не прозорість', () => {
      const background = declared(rules, headerChain(scheme, false), 'background-color');

      expect(background, 'заливки шапки в спокої немає').not.toBeNull();
      expect(colorOf(background, scheme)).not.toBe(themeSurface[scheme].body);
    });

    it('у спокої має нижню межу, а не `0px none`', () => {
      const border = declared(rules, headerChain(scheme, false), 'border-bottom');

      expect(border, 'нижньої межі шапки немає').not.toBeNull();
      expect(border).toMatch(/^[1-9]\d*px\s+solid\s/);
    });

    it('важча за рядок даних', () => {
      const weight = declared(rules, headerChain(scheme, false), 'font-weight');

      // 600 — вага ПАКЕТА, якої самої по собі виявилося замало; решта
      // застосунку (`<th>`) уже пише 700.
      expect(Number(weight ?? '400')).toBeGreaterThanOrEqual(700);
    });

    it('нижня межа відокремлює шапку видимо: ≥ 3:1 і до заливки шапки, і до поверхні даних', () => {
      const chain = headerChain(scheme, false);
      const line = colorOf(declared(rules, chain, 'border-bottom')?.split(/\s+/).slice(2).join(' ') ?? null, scheme);
      const fill = colorOf(declared(rules, chain, 'background-color'), scheme);

      expect(contrast(line, fill), `${line} на ${fill}`).toBeGreaterThanOrEqual(AA.nonText);
      expect(
        contrast(line, themeSurface[scheme].body),
        `${line} на ${themeSurface[scheme].body}`,
      ).toBeGreaterThanOrEqual(AA.nonText);
    });

    it('текст заголовка читається на власній заливці', () => {
      const chain = headerChain(scheme, false);
      const text = colorOf(declared(rules, chain, 'color'), scheme);
      const fill = colorOf(declared(rules, chain, 'background-color'), scheme);

      expect(contrast(text, fill), `${text} на ${fill}`).toBeGreaterThanOrEqual(AA.text);
    });

    /*
     * ⛔ Найважливіше твердження файлу: нова заливка спокою не сміє з'їсти
     * сигнал ОБРАНОЇ колонки. Правило пакета для обраної
     * (`revo-grid[theme=compact] revogr-header .rgHeaderCell.focused-cell`)
     * має специфічність, РІВНУ нашому правилу спокою, — тобто без власного
     * перевизначення переможець залежав би від порядку вставки стилів, якого
     * ніхто не гарантує.
     */
    it('обране лишається помітним поверх заливки спокою', () => {
      const focused = headerChain(scheme, true);
      const rest = headerChain(scheme, false);

      const focusedFill = colorOf(declared(rules, focused, 'background-color'), scheme);
      const restFill = colorOf(declared(rules, rest, 'background-color'), scheme);

      expect(focusedFill).not.toBe(restFill);

      // Другий носій, не сама лише заливка: різниця двох поверхонь мала, а
      // лінія акцентом читається й після знеколірення знімка.
      const focusedLine = declared(rules, focused, 'border-bottom-color');
      const restLine = declared(rules, rest, 'border-bottom');

      expect(focusedLine, 'колір лінії обраної колонки не перевизначено').not.toBeNull();
      expect(restLine).not.toContain(focusedLine ?? '');
    });
  });
});
