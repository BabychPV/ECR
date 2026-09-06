import { describe, expect, it } from 'vitest';
import { AA, contrast } from '../contrast';
import { deltaE00 } from '../colorScience';
import {
  expressionDark,
  expressionLight,
  monacoRules,
  paletteTokens,
  type ExpressionPalette,
} from '../expressionTheme';

/**
 * Палітра підсвічування виразів (`ФВ-9.15a`, `ФВ-14.17`).
 *
 * ⛔ Обчислення, а не око. Нечитабельне ключове слово в редакторі — не
 * косметика: людина не бачить, де закінчується її формула, і правúть не там.
 */

describe.each([
  ['світла', expressionLight],
  ['темна', expressionDark],
])('%s тема: кожен колір читається', (_name, palette: ExpressionPalette) => {
  it.each(paletteTokens)('%s дає щонайменше AA проти тла', (token) => {
    expect(contrast(palette[token], palette.background)).toBeGreaterThanOrEqual(AA.text);
  });
});

describe('ключ рядка проти числа', () => {
  // ⛔ Єдина пара, де колір є ЄДИНИМ носієм змісту. У `[7001001]` і в `1.5`
  // всередині предиката — ті самі цифри, і що з них ім'я, а що кількість,
  // каже тільки колір. Решту префіксів (`CST.`, `!`, `@`, `HDR.`) називає сам
  // текст, і там колір лише прискорює читання.
  //
  // ⚠ Поріг узятий той самий, що й для станів комірки (`D-144`): ΔE00 ≥ 20 у
  // нормальному зорі. Нижче цього два кольори читаються як відтінки одного.
  it.each([
    ['світла', expressionLight],
    ['темна', expressionDark],
  ])('%s тема розводить їх за тоном', (_name, palette: ExpressionPalette) => {
    expect(deltaE00(palette.type, palette.number)).toBeGreaterThanOrEqual(20);
  });
});

describe('правила для Monaco', () => {
  it('кольори віддаються без решітки', () => {
    // ⛔ З решіткою Monaco застосовує тему МОВЧКИ і без ефекту: ані помилки,
    // ані попередження — просто чорний текст. Причину шукали б у токенізаторі.
    for (const rule of monacoRules(expressionLight)) {
      expect(rule.foreground).toMatch(/^[0-9a-f]{6}$/);
    }
  });

  it('кожен клас токена з підсвічування має правило', () => {
    // ⚠ Клас без правила не помилка — він просто лишається кольором тексту.
    // Саме тому пропуск і треба ловити тестом: на екрані він виглядає як
    // «чомусь не виділяється», а не як щось зламане.
    const covered = new Set(monacoRules(expressionLight).map((rule) => rule.token));

    expect([...covered]).toEqual(
      expect.arrayContaining([
        'keyword',
        'predefined',
        'string',
        'number',
        'constant',
        'variable',
        'tag',
        'attribute.name',
        'type.identifier',
        'operator',
        'invalid',
      ]),
    );
  });
});
