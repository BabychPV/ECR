import { themeSurface } from './theme';

/**
 * Палітра підсвічування мови виразів (`ФВ-9.15a`).
 *
 * ⛔ Живе в `shared/theme`, бо це **єдине** місце застосунку, де дозволені
 * колірні літерали (`D-126`, гейт `no-restricted-syntax`). Monaco приймає лише
 * шістнадцяткові рядки — CSS-змінних його `defineTheme` не розуміє, — і саме
 * тому палітра не могла лишитися поруч із компонентом.
 *
 * ⚠ Що тут вимірюється і що НЕ вимірюється.
 *
 * **Контраст — вимірюється і є гейтом.** Кожен колір дає ≥ 4.5:1 проти тла
 * своєї теми (`AA.text`, `ФВ-14.17`). Нечитабельне ключове слово в редакторі —
 * не косметика: людина не бачить, де закінчується її формула.
 *
 * **Взаємна розрізнюваність — вимірюється лише там, де колір є ЄДИНИМ
 * носієм.** Це відрізняє палітру від станів комірки (`D-144`), де колір ніс усе
 * і тому кожну пару перевіряли на ΔE00 у трьох симуляціях дихромазії. Тут
 * `CST.`, `!`, `@` і `HDR.` названі самим текстом: їх ні з чим не сплутати
 * навіть у повністю сірому вигляді, і колір лише прискорює читання.
 *
 * ⛔ Виняток один і він справжній: **ключ рядка проти числа**. У `[7001001]` і
 * в `1.5` всередині предиката — ті самі цифри, і що з них ім'я, а що кількість,
 * каже ТІЛЬКИ колір. Тому ця пара розведена по тону (пурпуровий проти синього)
 * і перевірена на ΔE00.
 */

/** Класи токенів, які фарбує підсвічування. */
export interface ExpressionPalette {
  /** Тло редактора — те, проти чого рахується контраст. */
  readonly background: string;
  /** Основний текст. */
  readonly foreground: string;
  /** `AND`, `OR`, `NOT`, `WHERE`, `Period`. */
  readonly keyword: string;
  /** Імена функцій діалекту. */
  readonly predefined: string;
  /** Рядковий літерал. */
  readonly string: string;
  /** Число і `TRUE` / `FALSE` / `NULL`. */
  readonly number: string;
  /** Константа методології — `CST.`. */
  readonly constant: string;
  /** Аргумент рядка джерела — `@`. */
  readonly variable: string;
  /** Посилання на іншу формулу версії — `!`. */
  readonly tag: string;
  /** Поле шапки документа — `HDR.`. */
  readonly attribute: string;
  /** Код і ключ рядка всередині квадратних дужок. */
  readonly type: string;
  /** Оператори і роздільники. */
  readonly operator: string;
  /** Символ, якого в мові немає взагалі. */
  readonly invalid: string;
}

/** Світла палітра. */
export const expressionLight: ExpressionPalette = {
  background: themeSurface.light.body,
  foreground: '#1f1f1f',
  keyword: '#7b1fa2',
  predefined: '#8a4b00',
  string: '#0a6640',
  number: '#0a4a98',
  constant: '#6a1b9a',
  variable: '#a03000',
  tag: '#00695c',
  attribute: '#00588a',
  type: '#8a1c5c',
  operator: '#444444',
  invalid: '#b3170c',
};

/** Темна палітра. */
export const expressionDark: ExpressionPalette = {
  background: themeSurface.dark.body,
  foreground: '#e0e0e0',
  keyword: '#d4a0ff',
  predefined: '#f0b96b',
  string: '#7fd6a8',
  number: '#9fd0ff',
  constant: '#c58fff',
  variable: '#ffab7a',
  tag: '#66d9c8',
  attribute: '#7fc9f0',
  type: '#f09ac8',
  operator: '#b0b0b0',
  invalid: '#ff8f7a',
};

/** Кольори, які мусять читатися проти тла своєї теми. */
export const paletteTokens = [
  'foreground',
  'keyword',
  'predefined',
  'string',
  'number',
  'constant',
  'variable',
  'tag',
  'attribute',
  'type',
  'operator',
  'invalid',
] as const satisfies readonly (keyof ExpressionPalette)[];

/**
 * Правила Monaco: клас токена → колір.
 *
 * ⚠ Monaco очікує колір **без** решітки. Із нею тема застосовується мовчки і
 * без ефекту — жодної помилки в консолі, просто чорний текст, і причину
 * шукають у токенізаторі.
 */
export function monacoRules(
  palette: ExpressionPalette,
): readonly { token: string; foreground: string; fontStyle?: string }[] {
  const bare = (color: string): string => color.replace('#', '');

  return [
    { token: 'keyword', foreground: bare(palette.keyword) },
    { token: 'predefined', foreground: bare(palette.predefined) },
    { token: 'string', foreground: bare(palette.string) },
    { token: 'string.quote', foreground: bare(palette.string) },
    { token: 'string.escape', foreground: bare(palette.string), fontStyle: 'bold' },
    { token: 'number', foreground: bare(palette.number) },
    { token: 'number.float', foreground: bare(palette.number) },
    { token: 'constant.language', foreground: bare(palette.number) },
    { token: 'constant', foreground: bare(palette.constant) },
    { token: 'variable', foreground: bare(palette.variable) },
    { token: 'variable.predefined', foreground: bare(palette.variable) },
    { token: 'tag', foreground: bare(palette.tag) },
    { token: 'attribute.name', foreground: bare(palette.attribute) },
    { token: 'type.identifier', foreground: bare(palette.type) },
    { token: 'identifier', foreground: bare(palette.foreground) },
    { token: 'operator', foreground: bare(palette.operator) },
    { token: 'delimiter', foreground: bare(palette.operator) },
    { token: 'delimiter.square', foreground: bare(palette.operator) },
    { token: 'delimiter.parenthesis', foreground: bare(palette.operator) },
    { token: 'invalid', foreground: bare(palette.invalid), fontStyle: 'underline' },
  ];
}
