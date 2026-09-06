import type { ExpressionDialect } from './dialect';
import { allowsMethodologyRefs } from './dialect';

/**
 * Підсвічування мови виразів (`02b`) для Monaco.
 *
 * ⛔ **Це розфарбовування, а не перевірка.** Токенізатор Monarch — друга,
 * наближена реалізація лексики. Дозволити їй судити про правильність виразу
 * означало б завести другу правду про мову: редактор світив би зеленим те, що
 * публікація відхилить, і червоним те, що вона прийме. Про правильність каже
 * СЕРВЕР — тими самими перевірками, якими він відхиляє публікацію (`02b` §12).
 *
 * ⚠ Межа проведена так: тут вирішується лише **лексичний клас** токена.
 * `invalid` призначається виключно символам, яких у мові немає **взагалі** —
 * ніколи конструкції, що просто заборонена в цьому діалекті чи не резолвиться.
 * Тому `@Arg` у формулі шаблону тут звичайна змінна: заборонений він чи ні,
 * каже сервер, і він же пояснює чому (`Parser.cs`: «належить діалекту
 * методологій»).
 */

/** Ключові слова обох діалектів (`02b` §1). */
const Keywords = ['AND', 'OR', 'NOT', 'WHERE'] as const;

/** Літерали-константи мови (`02b` §1). */
const Literals = ['TRUE', 'FALSE', 'NULL'] as const;

/** Параметри побудови мови. */
export interface LanguageOptions {
  /** Діалект — визначає розбір `!` (`D-113`). */
  readonly dialect: ExpressionDialect;

  /**
   * Імена функцій діалекту — **з сервера** (`FunctionRegistry.Names`).
   *
   * ⛔ Не зашитий тут перелік із 12 і 24 назв (`02b` §7–§8). Зашитий означав би
   * другу правду про склад мови: додали функцію на сервері — редактор перестав
   * би її виділяти, і виглядало б це як помилка в тексті.
   *
   * ⚠ Порожній перелік — це «ще не завантажено», а НЕ «функцій немає». Доки
   * він порожній, жодне ім'я не позначається: відсутність знання не має
   * виглядати як знання про відсутність.
   */
  readonly functionNames: readonly string[];
}

/**
 * Будує визначення Monarch для діалекту.
 *
 * Повертає структуру, а не реєструє її: реєстрація потребує Monaco, який
 * важить сотні кілобайт і вантажиться окремим чанком (`D-132`). Чиста функція
 * перевіряється без нього.
 *
 * @param options Діалект і перелік функцій, отриманий із сервера.
 */
export function buildMonarchLanguage(options: LanguageOptions): MonarchLanguage {
  // ⚠ Верхній регістр: лексер сервера порівнює імена через
  // `StringComparer.OrdinalIgnoreCase` (`Lexer.cs`), тому `sum` і `SUM` — те
  // саме слово, а перелік має бути в одному регістрі, щоб `cases` спрацював.
  const functions = options.functionNames.map((name) => name.toUpperCase());

  // ⛔ Посилання на формулу `!Name` існує лише в діалекті методологій, і лише
  // коли за іменем НЕ йде дужка: `!SUM(x)` — це заперечення виклику, а не
  // формула на ім'я «SUM». Правило дослівно повторює `Parser.State.IsFormulaRef`
  // — єдину справжню неоднозначність граматики. Розійтися з ним означало б
  // пофарбувати заперечення як посилання рівно в тих виразах, де різниця
  // змінює результат.
  const formulaRef: readonly TokenRule[] = allowsMethodologyRefs(options.dialect)
    ? [[/![A-Za-z_]\w*(?!\s*\()/, 'tag']]
    : [];

  return {
    ignoreCase: true,
    keywords: [...Keywords],
    literals: [...Literals],
    functions,

    // ⛔ Правила коментаря НЕМАЄ навмисно: коментарів у мові не існує
    // (`02b` §1 — пояснення живуть у полі опису формули). Тому `--` у тексті
    // світиться як два мінуси, чим воно й є.
    brackets: [
      { open: '(', close: ')', token: 'delimiter.parenthesis' },
      { open: '[', close: ']', token: 'delimiter.square' },
    ],

    tokenizer: {
      root: [
        [/[ \t\r\n]+/, 'white'],

        // ── Посилання методології (`02b` §3.4) ─────────────────────────────
        // Префікс без імені підсвічується теж: користувач друкує посимвольно,
        // і саме на «CST.» він чекає на перелік підстановок.
        [/CST\.[A-Za-z_]\w*/, 'constant'],
        [/CST\./, 'constant'],
        [/@[A-Za-z_]\w*/, 'variable'],

        // ⚠ `!=` — ПЕРЕД посиланням на формулу, інакше `a != b` прочиталося б
        // як «a» і посилання «= b», і нерівність зникла б із мови.
        [/!=/, 'operator'],
        ...formulaRef,

        // ── Шапка документа: у ОБОХ діалектах (`02b` §3.3 п.4, §3.4) ───────
        [/HDR\.[A-Za-z_]\w*/, 'attribute.name'],
        [/HDR\./, 'attribute.name'],

        // ── Літерали ───────────────────────────────────────────────────────
        [/'/, { token: 'string.quote', next: '@string' }],
        [/\d+\.\d+/, 'number.float'],
        [/\d+/, 'number'],

        [/\[/, { token: 'delimiter.square', next: '@reference' }],

        // ── Виклик функції ─────────────────────────────────────────────────
        // ⚠ Невідоме ім'я лишається ЗВИЧАЙНИМ ідентифікатором, а не помилкою.
        // Відсутність виділення і є сигналом «такої функції я не знаю»; про те,
        // що це помилка, скаже сервер — і скаже, яка саме. Це важить: у чинному
        // шаблоні 429 викликів `VLOOKUP`, і кожен має пояснити себе текстом, а
        // не самим лише червоним кольором.
        [
          /[A-Za-z_]\w*(?=\s*\()/,
          { cases: { '@functions': 'predefined', '@default': 'identifier' } },
        ],

        [
          /[A-Za-z_]\w*/,
          {
            cases: {
              '@keywords': 'keyword',
              '@literals': 'constant.language',
              '@default': 'identifier',
            },
          },
        ],

        [/[()]/, '@brackets'],
        [/,/, 'delimiter'],
        [/(<=|>=|<>|==|&&|\|\||[-+*/%^&<>=?:!])/, 'operator'],
        [/./, 'invalid'],
      ],

      // ── Усередині «[...]»: посилання ─────────────────────────────────────
      // Код аркуша, ключ рядка, діапазон і зсув періоду (`02b` §3–§4).
      reference: [
        [/[ \t\r\n]+/, 'white'],

        // Зсув періоду: «Period», «Period:-1», «Period:+1» (`02b` §3.2).
        [/(Period)(\s*:\s*)([+-]?\d+)/, ['keyword', 'delimiter', 'number']],
        [/Period\b/, 'keyword'],

        // Підстановка поточної колонки (`02b` §1, `column_selector`).
        [/\{(Month|Period)\}/, 'variable.predefined'],

        // ⛔ `switchTo`, а не `next`: предикат ЗАМІНЮЄ стан посилання, а не
        // вкладається в нього. Інакше його «]» повернув би нас у посилання
        // замість виразу, і решта формули дофарбовувалася б за правилами
        // квадратних дужок.
        [/WHERE\b/, { token: 'keyword', switchTo: '@predicate' }],

        [/\]/, { token: 'delimiter.square', next: '@pop' }],
        [/:/, 'delimiter'],

        // ⚠ Один клас і для коду (`EcrCode`, `R-B6`), і для ключа рядка
        // (`7001001`, GUID із дефісами): усередині дужок читач має бачити їх
        // однаково незалежно від того, з літери токен чи з цифри. Число тут
        // НЕ число — `7001001` це ім'я рядка, і фарбувати його як кількість
        // означало б підказувати, що з ним можна рахувати.
        [/[A-Za-z_0-9][\w-]*/, 'type.identifier'],

        [/./, 'invalid'],
      ],

      // ── Усередині «[WHERE ...]»: предикат динамічного діапазону ──────────
      // Звичайний вираз (`02b` §4.2), тому числа тут — числа.
      predicate: [
        [/[ \t\r\n]+/, 'white'],
        [/'/, { token: 'string.quote', next: '@string' }],
        [/\d+\.\d+/, 'number.float'],
        [/\d+/, 'number'],

        // Посилання на колонку того самого рядка: `[Category] = 'Fuel'`.
        [/\[/, { token: 'delimiter.square', next: '@reference' }],
        [/\]/, { token: 'delimiter.square', next: '@pop' }],

        [
          /[A-Za-z_]\w*/,
          {
            cases: {
              '@keywords': 'keyword',
              '@literals': 'constant.language',
              '@default': 'identifier',
            },
          },
        ],

        [/(<=|>=|<>|!=|==|&&|\|\||[-+*/%^&<>=])/, 'operator'],
        [/[(),]/, 'delimiter'],
        [/./, 'invalid'],
      ],

      // ── Рядковий літерал ─────────────────────────────────────────────────
      // Екранування — ПОДВОЄННЯ лапки (`02b` §1), не зворотна коса.
      string: [
        [/''/, 'string.escape'],
        [/'/, { token: 'string.quote', next: '@pop' }],
        [/[^']+/, 'string'],
      ],
    },
  };
}

/**
 * Налаштування мови: дужки і автозакриття.
 *
 * ⚠ Лапка не автозакривається всередині рядка: інакше подвоєння `''` — єдиний
 * спосіб екранувати лапку в нашій мові — щоразу давало б чотири лапки.
 */
export function buildLanguageConfiguration(): LanguageConfiguration {
  return {
    brackets: [
      ['(', ')'],
      ['[', ']'],
    ],
    autoClosingPairs: [
      { open: '(', close: ')' },
      { open: '[', close: ']' },
      { open: "'", close: "'", notIn: ['string'] },
    ],
    surroundingPairs: [
      { open: '(', close: ')' },
      { open: '[', close: ']' },
      { open: "'", close: "'" },
    ],
    // ⛔ `comments` не оголошено навмисно. З ним Monaco дає Ctrl+/, який
    // вставив би синтаксис, якого в мові немає, — і вираз ставав би
    // неопубліковуваним одним натиском, без жодного натяку чому.
  };
}

// ─────────────────────────────────────────────────────────────────────────────
// Локальні типи
// ─────────────────────────────────────────────────────────────────────────────

/** Дія над токеном у правилі Monarch. */
type TokenAction =
  | string
  | readonly string[]
  | { readonly token: string; readonly next?: string; readonly switchTo?: string }
  | { readonly cases: Readonly<Record<string, string>> };

/** Одне правило токенізації: зразок і дія. */
export type TokenRule = readonly [RegExp, TokenAction];

/**
 * Форма визначення Monarch, якою користується цей модуль.
 *
 * ⚠ Оголошено тут, а не взято з `monaco-editor`: імпорт типів із Monaco
 * потягнув би його в граф модуля, а з ним — рішення про те, в якому чанку
 * опиниться цей файл. Структура віддається як дані і приводиться до
 * `monaco.languages.IMonarchLanguage` у місці реєстрації.
 */
export interface MonarchLanguage {
  readonly ignoreCase: boolean;
  readonly keywords: readonly string[];
  readonly literals: readonly string[];
  readonly functions: readonly string[];
  readonly brackets: readonly { open: string; close: string; token: string }[];
  readonly tokenizer: Readonly<Record<string, readonly TokenRule[]>>;
}

/** Форма налаштувань мови, якою користується цей модуль. */
export interface LanguageConfiguration {
  readonly brackets: readonly (readonly [string, string])[];
  readonly autoClosingPairs: readonly {
    readonly open: string;
    readonly close: string;
    readonly notIn?: readonly string[];
  }[];
  readonly surroundingPairs: readonly { readonly open: string; readonly close: string }[];
}
