import { describe, expect, it } from 'vitest';
import type { ExpressionMetadataDto } from '@/api/types';
import { completionAt, completionsFor, signatureOf } from '../completion';

/**
 * Автодоповнення `CST.`, `!`, `@`, `HDR.` (`ФВ-9.15a`).
 *
 * ⚠ Перевіряються чисті функції, а не редактор. Рендер важкого компонента в
 * jsdom іде хвилинами (`D1-12`), і тест через Monaco вимкнули б першим. Тут
 * перевіряється рівно те, що ламається: який префікс розпізнано і що він
 * пропонує.
 */

const metadata: ExpressionMetadataDto = {
  functions: [
    { name: 'SUM', minArgs: 1, maxArgs: null, acceptsRange: true, resultType: 'Number', tier: 'Core' },
    { name: 'ROUND', minArgs: 2, maxArgs: 2, acceptsRange: false, resultType: 'Number', tier: 'Core' },
    {
      name: 'SUBSTANCE',
      minArgs: 1,
      maxArgs: 1,
      acceptsRange: false,
      resultType: 'Number',
      tier: 'Extension',
    },
  ],
  constants: [{ name: 'EF_CO2', unit: 'kg_per_t', note: 'Fuel' }],
  formulas: [{ name: 'BaseEmission', unit: 'kg', note: null }],
  arguments: [{ name: 'FuelConsumption', unit: 't', note: 'Decimal' }],
  headers: [{ name: 'Train', unit: null, note: null }],
};

describe('що доповнюється в позиції', () => {
  it.each([
    ['CST.', 'constant'],
    ['HDR.', 'header'],
    ['!', 'formula'],
    ['@', 'argument'],
  ])('префікс «%s» розпізнається як %s', (prefix, kind) => {
    const text = `1 + ${prefix}`;

    expect(completionAt(text, text.length)?.kind).toBe(kind);
  });

  it('уже набране ім’я віддається окремо — за ним фільтрують перелік', () => {
    const context = completionAt('@Fuel', 5);

    expect(context).toEqual({ kind: 'argument', replaceFrom: 1, typed: 'Fuel' });
  });

  it('порожнє місце пропонує функції', () => {
    // ⚠ Перелік має відкриватися по Ctrl+Space на порожньому місці, а не лише
    // після першої літери: інакше про автодоповнення дізнається тільки той,
    // хто про нього вже знає.
    expect(completionAt('', 0)?.kind).toBe('function');
  });

  it('усередині рядкового літерала не доповнюється нічого', () => {
    // ⛔ `'@Fuel'` — це ТЕКСТ, а не посилання. Підставити туди ім'я аргументу
    // означало б мовчки зіпсувати константу, яку користувач саме друкує.
    expect(completionAt("'@Fuel", 6)).toBeNull();
  });

  it('подвоєна лапка закриває літерал, а не відкриває новий', () => {
    // ⛔ `''` — єдиний спосіб екранувати лапку в нашій мові (`02b` §1).
    // Наївний підрахунок непарності зламався б саме тут і вважав би решту
    // виразу текстом — тобто мовчки вимкнув би доповнення до кінця рядка.
    const text = "'don''t' + @";

    expect(completionAt(text, text.length)?.kind).toBe('argument');
  });
});

describe('склад переліку', () => {
  it('символи беруться зі свого виду', () => {
    const items = completionsFor({ kind: 'constant', replaceFrom: 0, typed: '' }, metadata);

    expect(items.map((i) => i.insert)).toEqual(['EF_CO2']);
    expect(items[0]?.detail).toBe('kg_per_t');
  });

  it('набране ім’я звужує перелік без огляду на регістр', () => {
    const items = completionsFor({ kind: 'function', replaceFrom: 0, typed: 'su' }, metadata);

    expect(items.map((i) => i.insert)).toEqual(['SUBSTANCE', 'SUM']);
  });

  it('ярус приходить із сервера і доїжджає до варіанта підстановки', () => {
    // ⛔ `Extension` означає «чинний рушій цього не вміє»: у версії з
    // `NumericMode = Legacy` вираз із такою функцією не опублікується
    // (`ECR-CALC-0433`). Позначку малює місце реєстрації провайдера, але
    // ЗНАННЯ про ярус мусить дійти сюди з сервера — інакше редактор його
    // вигадував би із зашитого переліку.
    const items = completionsFor({ kind: 'function', replaceFrom: 0, typed: '' }, metadata);

    expect(items.find((i) => i.insert === 'SUBSTANCE')?.tier).toBe('Extension');
    expect(items.find((i) => i.insert === 'SUM')?.tier).toBe('Core');
  });

  it('без метаданих перелік порожній, а не вигаданий', () => {
    // ⛔ Порожньо, доки сервер не відповів. Зашитий запасний перелік означав би
    // другу правду про склад мови: він пережив би зміну на сервері і почав би
    // підказувати те, чого вже немає.
    expect(completionsFor({ kind: 'function', replaceFrom: 0, typed: '' }, undefined)).toEqual([]);
  });
});

describe('підказка сигнатури', () => {
  it('агрегат оголошує, що приймає діапазон', () => {
    // ⚠ `SUM([7001001:7001005])` — головна форма виклику агрегата в шаблоні.
    // Підказка, яка про діапазон мовчить, навчала б передавати комірки по одній
    // — тобто рівно тій формі, від якої відходить уся міграція з Excel.
    expect(signatureOf(metadata.functions[0]!)).toBe('SUM(range | number, …)');
  });

  it('функція з двома обов’язковими аргументами показує обидва', () => {
    expect(signatureOf(metadata.functions[1]!)).toBe('ROUND(arg1, arg2)');
  });
});
