import { describe, expect, it } from 'vitest';
import { buildLanguageConfiguration, buildMonarchLanguage, type TokenRule } from '../language';

/**
 * Підсвічування мови виразів (`ФВ-9.15a`).
 *
 * ⚠ Перевіряються самі ПРАВИЛА, а не робота Monaco. Причина технічна і
 * названа: `monaco.editor.tokenize` у jsdom падає в службі тем ще до першого
 * токена (`iconsStyleSheet` звертається до CSS, якого в jsdom немає). Тому
 * фарбування в русі перевіряється в справжньому браузері, а тут — регулярні
 * вирази і їхній порядок, тобто рівно те, що править людина.
 */

/** Правила стану `root` для діалекту. */
function rootRules(dialect: 'Template' | 'Methodology', functions: string[] = []): TokenRule[] {
  return [...buildMonarchLanguage({ dialect, functionNames: functions }).tokenizer['root']!];
}

/** Правило, прив'язане до початку тексту. */
function anchored(rule: TokenRule): RegExp {
  return new RegExp(`^(?:${rule[0].source})`, rule[0].flags.replace('g', ''));
}

/** Перше правило, чий зразок збігається з початком тексту. */
function firstMatch(rules: readonly TokenRule[], text: string): TokenRule | undefined {
  return rules.find((rule) => anchored(rule).test(text));
}

/**
 * Скільки тексту з'їдає перше правило, що збіглося.
 *
 * ⛔ Саме довжина, а не факт збігу: правило `@[A-Za-z_]\w*` збігається з
 * `@GCV.Temperature` теж — воно просто бере з нього `@GCV`. Тест, який питає
 * лише «яке правило спрацювало», такої різниці не побачив би, а на екрані вона
 * означає інший зміст виразу.
 */
function consumed(rules: readonly TokenRule[], text: string): string | undefined {
  const rule = firstMatch(rules, text);

  return rule === undefined ? undefined : (anchored(rule).exec(text)?.[0] ?? undefined);
}

describe('посилання на формулу «!» — єдина справжня неоднозначність граматики', () => {
  // ⛔ Правило дослівно повторює `Parser.State.IsFormulaRef` на сервері:
  // `!X` — це посилання на формулу ЛИШЕ в діалекті методологій і ЛИШЕ коли за
  // іменем немає дужки. Розійтися з ним означало б пофарбувати заперечення як
  // посилання рівно у виразах, де різниця змінює результат.

  it('у методологіях «!BaseEmission» — посилання', () => {
    expect(firstMatch(rootRules('Methodology'), '!BaseEmission')?.[1]).toBe('tag');
  });

  it('у методологіях «!SUM(x)» — заперечення виклику, а не формула на ім’я SUM', () => {
    expect(firstMatch(rootRules('Methodology'), '!SUM(x)')?.[1]).toBe('operator');
  });

  it('у шаблонах «!X» — завжди заперечення: посилань на формули там немає', () => {
    expect(firstMatch(rootRules('Template'), '!BaseEmission')?.[1]).toBe('operator');
  });

  it('«!=» лишається нерівністю в обох діалектах', () => {
    // ⚠ Порядок правил тут навантажений: якби посилання перевірялося раніше,
    // `a != b` прочиталося б як «a» і посилання «= b», і нерівність зникла б
    // із мови.
    for (const dialect of ['Template', 'Methodology'] as const) {
      expect(firstMatch(rootRules(dialect), '!= 1')?.[1]).toBe('operator');
    }
  });
});

describe('дотове ім’я аргументу — ОДНЕ посилання (I.6)', () => {
  // ⛔ Правило дослівно повторює `Lexer.ReadDottedName` на сервері. Розійтися
  // з ним тут означає рівно те, чого цей файл забороняє собі в шапці:
  // редактор показує інший зміст, ніж бачить парсер. У корпусі методологій
  // таких імен 2534 — тобто розбіжність була б видима на кожному другому
  // виразі.

  it('ФВ-9.15a: «@GCV.HSE400_FG_Makat_Methane» фарбується цілком', () => {
    const rules = rootRules('Methodology');

    expect(consumed(rules, '@GCV.HSE400_FG_Makat_Methane')).toBe(
      '@GCV.HSE400_FG_Makat_Methane',
    );
    expect(firstMatch(rules, '@GCV.HSE400_FG_Makat_Methane')?.[1]).toBe('variable');
  });

  it('кілька крапок поспіль теж одне ім’я', () => {
    expect(consumed(rootRules('Methodology'), '@A.B.C')).toBe('@A.B.C');
  });

  it('ім’я без крапки не змінилося', () => {
    expect(consumed(rootRules('Methodology'), '@Land_Measure_Component_337')).toBe(
      '@Land_Measure_Component_337',
    );
  });

  it('крапка перед цифрою іменем не є — лексер зупиняється там само', () => {
    // ⚠ `@X.5` — це аргумент `@X`, крапка і число. З'їсти тут `.5` означало б
    // пофарбувати як ім'я те, що сервер прочитає інакше.
    expect(consumed(rootRules('Methodology'), '@X.5')).toBe('@X');
  });

  it('крапка в кінці не втягується', () => {
    expect(consumed(rootRules('Methodology'), '@X.')).toBe('@X');
  });

  it('у шаблонах правило те саме: діалект тут ні до чого', () => {
    // Дотових імен у діалекті A не буває, але правило спільне, і різна
    // поведінка означала б другу точку задання.
    expect(consumed(rootRules('Template'), '@GCV.Temperature')).toBe('@GCV.Temperature');
  });
});

describe('склад мови приходить іззовні', () => {
  it('відомі функції виділяються', () => {
    const rules = rootRules('Template', ['SUM']);
    const rule = firstMatch(rules, 'SUM(');

    expect(rule?.[1]).toMatchObject({ cases: { '@functions': 'predefined' } });
  });

  it('імена нормалізуються до верхнього регістру', () => {
    // ⚠ Лексер сервера порівнює імена `OrdinalIgnoreCase`, тож `sum` і `SUM` —
    // те саме слово. Перелік у різних регістрах не збігся б із `cases`.
    expect(buildMonarchLanguage({ dialect: 'Template', functionNames: ['sum'] }).functions).toEqual(
      ['SUM'],
    );
  });

  it('невідоме ім’я лишається звичайним ідентифікатором, а не помилкою', () => {
    // ⛔ Відсутність виділення і є сигналом «такої функції я не знаю». Червоне
    // тут означало б, що редактор САМ судить про правильність — при тому, що
    // склад мови він щойно отримав із мережі і міг не отримати зовсім.
    // Про помилку каже сервер, і він же каже, яка саме.
    const rules = rootRules('Template', ['SUM']);
    const rule = firstMatch(rules, 'VLOOKUP(');

    expect(rule?.[1]).toMatchObject({ cases: { '@default': 'identifier' } });
  });
});

describe('чого в мові немає', () => {
  it('правила коментаря не існує в жодному стані', () => {
    // ⛔ Коментарів у мові немає (`02b` §1). Правило для них означало б, що
    // «--» перестає бути двома мінусами.
    const language = buildMonarchLanguage({ dialect: 'Methodology', functionNames: [] });
    const rules = Object.values(language.tokenizer).flat();

    expect(rules.filter((rule) => String(rule[1]).includes('comment'))).toEqual([]);
  });

  it('перемикання на коментар не оголошене в налаштуваннях мови', () => {
    // ⚠ Без цього Ctrl+/ вставив би синтаксис, якого мова не приймає, — і вираз
    // ставав би неопубліковуваним одним натиском.
    expect(buildLanguageConfiguration()).not.toHaveProperty('comments');
  });
});

describe('рядковий літерал', () => {
  it('екранування — подвоєння лапки, а не зворотна коса', () => {
    // ⛔ `02b` §1. Правило подвоєння мусить стояти ПЕРШИМ у стані рядка:
    // інакше `''` закриє літерал замість того, щоб дати одну лапку всередині.
    const state = buildMonarchLanguage({
      dialect: 'Template',
      functionNames: [],
    }).tokenizer['string']!;

    expect(state[0]?.[0].source).toBe("''");
    expect(state[0]?.[1]).toBe('string.escape');
  });
});

describe('усередині квадратних дужок', () => {
  it('ключ рядка — це ім’я, а не число', () => {
    // ⛔ `[7001001]` — ім'я рядка. Пофарбувати його як кількість означало б
    // підказувати, що з ним можна рахувати.
    const state = buildMonarchLanguage({
      dialect: 'Template',
      functionNames: [],
    }).tokenizer['reference']!;

    expect(firstMatch(state, '7001001]')?.[1]).toBe('type.identifier');
  });

  it('предикат ЗАМІНЮЄ стан посилання, а не вкладається в нього', () => {
    // ⛔ З `next` замість `switchTo` закривна дужка предиката повернула б
    // токенізатор у стан посилання замість виразу, і решта формули
    // дофарбовувалася б за правилами квадратних дужок.
    const state = buildMonarchLanguage({
      dialect: 'Template',
      functionNames: [],
    }).tokenizer['reference']!;

    expect(firstMatch(state, 'WHERE RowKind = 1]')?.[1]).toMatchObject({
      switchTo: '@predicate',
    });
  });
});
