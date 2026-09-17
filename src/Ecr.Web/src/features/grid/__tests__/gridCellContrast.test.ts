import { describe, it, expect } from 'vitest';
import { AA, contrast, flatten } from '@/shared/theme/contrast';
import { cellState, themeSurface } from '@/shared/theme/theme';
import {
  AppCss,
  PackageCss,
  declared,
  gridCascade,
  parseCss,
  resolveVar,
  type El,
} from './cssCascade';

/**
 * Контраст тексту в ЖИВІЙ комірці сітки (`ФВ-14.17`, `ФВ-14.18`).
 *
 * ⛔ Чому цього тесту бракувало і що саме пропустили. `contrast.test.ts`
 * перевіряв пару «фон стану» + `themeSurface[scheme].text` — тобто ПРИПУСКАВ,
 * що колір тексту комірки задає наша тема. Не задає: його задає пакет
 * (`revo-grid[theme=compact] revogr-data .rgCell { color: rgba(0,0,0,0.87) }`),
 * і в темній темі це давало **1.35:1** на фоні `#242424`. Введене оператором
 * значення було фактично невидиме, а обчислене — читалося ідеально, бо пакет
 * ставить `.rgCell.disabled { background-color: #f7f7f7 }` (світлий фон у
 * ТЕМНІЙ темі). Результат був вивернутий: видно те, чого не змінити, і не
 * видно того, що щойно ввели.
 *
 * ⛔ Другий дефект того самого кореня: `#f7f7f7` з `.rgCell.disabled` має
 * специфічність (0,3,2) проти (0,1,0) у `.ecr-cell--calculated`, тож заливка
 * СТАНУ програвала пакету взагалі. П'ять станів, виміряних і розведених за
 * ΔE00 у `theme.ts`, на readonly-комірках зливалися в один сірий.
 *
 * ⚠ axe тут допомогти не може принципово: правило `color-contrast` малює
 * елемент на `<canvas>`, якого в jsdom немає (`test/a11y.ts`). Тому доказ —
 * обчислення над САМИМИ таблицями стилів, включно з тією, що лежить у
 * `node_modules`.
 */

const Schemes = ['light', 'dark'] as const;
type Scheme = (typeof Schemes)[number];

/** `readOnly` → `read-only`. */
function kebab(name: string): string {
  return name.replace(/[A-Z]/g, (letter) => `-${letter.toLowerCase()}`);
}

/**
 * Комірки, для яких RevoGrid додає власний клас `disabled`
 * (`column.service.js`: `{ [DISABLED_CLASS]: this.isReadOnly(r, c) }`).
 * Саме ці два стани означають «сервер редагування не дозволив».
 */
const DisabledStates = new Set(['readOnly', 'calculated']);

const StateNames = Object.keys(cellState) as (keyof typeof cellState)[];

/** Корінь документа: схему задає Mantine атрибутом на `<html>`. */
function rootOf(scheme: Scheme): El {
  return {
    tag: 'html',
    isRoot: true,
    attrs: { 'data-mantine-color-scheme': scheme },
  };
}

/**
 * Ланцюжок предків комірки — рівно той, що будує RevoGrid.
 *
 * ⚠ Клас `ecr-cell` для комірки БЕЗ стану навмисно відсутній: `gridColumns`
 * у `DocumentGrid.tsx` повертає порожній `cellProperties` (`return {}`), коли
 * немає ані стану, ані маркера, ані стилю автора. Саме така комірка —
 * звичайна редагована — і була виміряна на 1.35:1, тож перевизначення, що
 * спирається на `.ecr-cell`, її б не зачепило.
 */
function cellChain(scheme: Scheme, state: keyof typeof cellState | null): El[] {
  const classes = ['rgCell'];
  if (state !== null) classes.push('ecr-cell', `ecr-cell--${kebab(state)}`);
  if (state !== null && DisabledStates.has(state)) classes.push('disabled');

  return [
    rootOf(scheme),
    { tag: 'revo-grid', attrs: { theme: 'compact' } },
    { tag: 'revogr-data' },
    { tag: 'div', classes: ['rgRow'] },
    { tag: 'div', classes },
  ];
}

/** Фактичні колір тексту і фон комірки після каскаду. */
function paint(
  rules: ReturnType<typeof gridCascade>,
  scheme: Scheme,
  state: keyof typeof cellState | null,
): { text: string; background: string } {
  const chain = cellChain(scheme, state);
  const root = rootOf(scheme);
  const surface = themeSurface[scheme];

  const rawBackground = declared(rules, chain, 'background-color');
  const background = flatten(
    rawBackground === null ? surface.body : (resolveVar(rules, root, rawBackground) ?? surface.body),
    surface.body,
  );

  // Колір не оголошений — успадковується від тіла сторінки, яке фарбує Mantine.
  const rawText = declared(rules, chain, 'color');
  const text = flatten(
    rawText === null ? surface.text : (resolveVar(rules, root, rawText) ?? surface.text),
    background,
  );

  return { text, background };
}

describe('Контраст тексту в комірці сітки (ФВ-14.17)', () => {
  const rules = gridCascade();

  describe.each(Schemes)('схема «%s»', (scheme) => {
    it('звичайна редагована комірка: текст оператора читається', () => {
      const { text, background } = paint(rules, scheme, null);

      expect(contrast(text, background), `${text} на ${background}`).toBeGreaterThanOrEqual(
        AA.text,
      );
    });

    it.each(StateNames)('комірка в стані «%s»: текст читається', (name) => {
      const { text, background } = paint(rules, scheme, name);

      expect(contrast(text, background), `${text} на ${background}`).toBeGreaterThanOrEqual(
        AA.text,
      );
    });

    it.each(StateNames)('заливка стану «%s» переживає власні стилі RevoGrid', (name) => {
      // ⛔ Не косметика: без цього `readOnly` і `calculated` отримували
      // `#f7f7f7` з `.rgCell.disabled` в ОБОХ темах, тобто два різні стани
      // виглядали однаково, і вся робота з розведення палітри за ΔE00
      // (`theme.ts`, `D-144`) на них не діяла взагалі.
      expect(paint(rules, scheme, name).background).toBe(cellState[name][scheme].bg);
    });

    it('редактор комірки: те, що оператор ДРУКУЄ, теж читається', () => {
      // `revogr-edit` — накладка ПОРУЧ із коміркою, не всередині неї, тож
      // клас стану на неї не діє, а пакет задає їй `background-color: #fff`
      // намертво. У темній темі успадкований `#c9c9c9` на білому давав 1.66:1.
      const root = rootOf(scheme);
      const surface = themeSurface[scheme];
      const editor: El[] = [root, { tag: 'revo-grid', attrs: { theme: 'compact' } }, { tag: 'revogr-edit' }];
      const input: El[] = [...editor, { tag: 'input' }];

      const rawBackground = declared(rules, input, 'background-color')
        ?? declared(rules, editor, 'background-color');
      const background = flatten(
        rawBackground === null ? surface.body : (resolveVar(rules, root, rawBackground) ?? surface.body),
        surface.body,
      );

      const rawText = declared(rules, input, 'color') ?? declared(rules, editor, 'color');
      const text = flatten(
        rawText === null ? surface.text : (resolveVar(rules, root, rawText) ?? surface.text),
        background,
      );

      expect(contrast(text, background), `${text} на ${background}`).toBeGreaterThanOrEqual(
        AA.text,
      );
    });
  });

  /**
   * Калібрування самої перевірки. Без нього тест міг би бути зеленим тому, що
   * резолвер нічого не знаходить, а не тому, що контраст виправлений.
   */
  it('контроль: самого пакета досі не досить — саме наш CSS тримає гейт', () => {
    // Каскад БЕЗ нашої таблиці стилів — тобто те, що було до цього
    // виправлення. Якщо колись хтось прибере перевизначення з
    // `cell-states.css`, тести вище почервоніють, а цей рядок пояснить чому.
    const packageOnly = parseCss(PackageCss);
    const chain = cellChain('dark', null);

    const rawText = declared(packageOnly, chain, 'color') ?? '';
    expect(rawText).toBe('rgba(0, 0, 0, 0.87)');

    const onDarkBody = flatten(rawText, themeSurface.dark.body);
    expect(contrast(onDarkBody, themeSurface.dark.body)).toBeCloseTo(1.35, 1);

    // І заливка стану так само програвала пакету: `.rgCell.disabled`.
    expect(declared(packageOnly, cellChain('dark', 'calculated'), 'background-color')).toBe(
      '#f7f7f7',
    );

    // Наша таблиця стилів існує і перебиває обидва оголошення.
    expect(parseCss(AppCss).length).toBeGreaterThan(0);
  });
});
