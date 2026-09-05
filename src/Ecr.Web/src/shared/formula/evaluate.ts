/**
 * Клієнтський обчислювач — **лише підказка** під час введення; збережене
 * значення завжди рахує сервер (D-20).
 *
 * ⚠ Власний розбір, а не `formulajs`. Бібліотека дає функції, але не парсер, і
 * — головне — має **іншу семантику порожнечі**: `SUM` у ній вважає `null`
 * нулем, `AVERAGE` порожньої множини повертає `#DIV/0!`, а `1 / 0` — теж
 * `#DIV/0!` замість нашого `#DIV/0`. Взяти її означало б навмисно закласти
 * розбіжність між підказкою і збереженим числом. А підказка, яка систематично
 * розходиться з результатом, гірша за відсутню: користувач перестає вірити
 * обом.
 *
 * Набір випадків спільний із серверним тестом еквівалентності
 * (`tests/Ecr.TestKit/Fixtures/expression-equivalence.json`).
 */

/** Помилка обчислення в термінах нашої мови. */
export interface FormulaError {
  readonly error: string;
}

/** Значення виразу: число, текст, булеве, порожнеча або помилка. */
export type FormulaValue = number | string | boolean | null | FormulaError;

/**
 * Чи є значення помилкою.
 *
 * ⚠ Приймає `unknown`, а не `FormulaValue`: тим самим предикатом звужуються
 * і проміжні результати (`number[] | FormulaError`). Другий предикат для них
 * означав би два визначення того, що таке помилка.
 */
export function isError(value: unknown): value is FormulaError {
  return typeof value === 'object' && value !== null && 'error' in value;
}

const DivideByZero: FormulaError = { error: '#DIV/0' };
const Value: FormulaError = { error: '#VALUE' };
const Name: FormulaError = { error: '#NAME' };

/**
 * Обчислює вираз.
 *
 * ⚠ Синтаксична помилка — це **результат**, а не виняток: користувач друкує
 * формулу посимвольно, і половина проміжних станів синтаксично невалідна.
 * Кидати з них винятки означало б засипати консоль і зупиняти підказку.
 */
export function evaluate(expression: string): FormulaValue {
  try {
    const parser = new Parser(tokenize(expression));
    const value = parser.parseExpression();
    parser.expectEnd();

    return value;
  } catch {
    return Name;
  }
}

/** Форматує значення так само, як його показує сервер. */
export function format(value: FormulaValue): string {
  if (isError(value)) return value.error;
  if (value === null) return '';
  if (typeof value === 'boolean') return value ? 'true' : 'false';
  if (typeof value === 'number') return formatNumber(value);

  return value;
}

/**
 * Форматує число.
 *
 * ⚠ Двійковий дріб згладжується до п'ятнадцяти значущих цифр: `0.1 + 0.2` у
 * IEEE-754 дає `0.30000000000000004`, і показати це користувачеві означало б
 * оголосити підказку зламаною. Сервер рахує в `decimal` і показує `0.3`
 * (D-30) — підказка має збігатися.
 */
function formatNumber(value: number): string {
  if (!Number.isFinite(value)) return '#NUM';

  const rounded = Number.parseFloat(value.toPrecision(15));

  return Object.is(rounded, -0) ? '0' : String(rounded);
}

// ─────────────────────────────────────────────────────────────────────────────
// Лексер
// ─────────────────────────────────────────────────────────────────────────────

type TokenKind = 'number' | 'text' | 'name' | 'punct' | 'end';

interface Token {
  readonly kind: TokenKind;
  readonly value: string;
}

const Punctuation = ['<=', '>=', '<>', '(', ')', ',', '+', '-', '*', '/', '%', '^', '&', '=', '<', '>', '?', ':'];

function tokenize(input: string): Token[] {
  const tokens: Token[] = [];
  let index = 0;

  while (index < input.length) {
    const symbol = input[index];

    if (symbol === undefined || /\s/.test(symbol)) {
      index++;
      continue;
    }

    if (symbol === "'" || symbol === '"') {
      const closing = input.indexOf(symbol, index + 1);
      if (closing < 0) throw new SyntaxError('Незакритий рядковий літерал.');

      tokens.push({ kind: 'text', value: input.slice(index + 1, closing) });
      index = closing + 1;
      continue;
    }

    if (/[0-9]/.test(symbol)) {
      const match = /^[0-9]+(\.[0-9]+)?/.exec(input.slice(index));
      if (match === null) throw new SyntaxError('Некоректне число.');

      tokens.push({ kind: 'number', value: match[0] });
      index += match[0].length;
      continue;
    }

    if (/[A-Za-z_]/.test(symbol)) {
      const match = /^[A-Za-z_][A-Za-z0-9_.]*/.exec(input.slice(index));
      if (match === null) throw new SyntaxError('Некоректне ім’я.');

      tokens.push({ kind: 'name', value: match[0] });
      index += match[0].length;
      continue;
    }

    const punct = Punctuation.find((p) => input.startsWith(p, index));
    if (punct === undefined) throw new SyntaxError(`Невідомий символ «${symbol}».`);

    tokens.push({ kind: 'punct', value: punct });
    index += punct.length;
  }

  tokens.push({ kind: 'end', value: '' });

  return tokens;
}

// ─────────────────────────────────────────────────────────────────────────────
// Парсер-обчислювач
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Розбирає і одразу обчислює.
 *
 * ⚠ Без окремого AST: клієнтові дерево не потрібне — підказка живе рівно один
 * натиск клавіші. Сервер будує AST, бо йому потрібні залежності, топологічний
 * порядок і перевірка одиниць; повторювати це на клієнті означало б другу
 * реалізацію тих самих правил, яка мовчки розійдеться з першою.
 */
class Parser {
  private position = 0;

  constructor(private readonly tokens: Token[]) {}

  expectEnd(): void {
    if (this.current.kind !== 'end') throw new SyntaxError('Зайві символи в кінці виразу.');
  }

  /** Найнижчий пріоритет: тернарний оператор, правоасоціативний. */
  parseExpression(): FormulaValue {
    const condition = this.parseOr();

    if (!this.match('?')) return condition;

    const whenTrue = this.parseExpression();
    this.expect(':');
    const whenFalse = this.parseExpression();

    if (isError(condition)) return condition;

    return truthy(condition) ? whenTrue : whenFalse;
  }

  private parseOr(): FormulaValue {
    let left = this.parseAnd();

    while (this.matchName('OR')) {
      const right = this.parseAnd();
      left = logical(left, right, (a, b) => a || b);
    }

    return left;
  }

  private parseAnd(): FormulaValue {
    let left = this.parseNot();

    while (this.matchName('AND')) {
      const right = this.parseNot();
      left = logical(left, right, (a, b) => a && b);
    }

    return left;
  }

  private parseNot(): FormulaValue {
    if (this.matchName('NOT')) {
      const operand = this.parseNot();
      if (isError(operand)) return operand;
      if (operand === null) return null;

      return !truthy(operand);
    }

    return this.parseComparison();
  }

  private parseComparison(): FormulaValue {
    let left = this.parseConcat();

    for (;;) {
      const operator = ['<=', '>=', '<>', '=', '<', '>'].find((o) => this.check(o));
      if (operator === undefined) return left;

      this.advance();
      const right = this.parseConcat();
      left = compare(operator, left, right);
    }
  }

  private parseConcat(): FormulaValue {
    let left = this.parseAdditive();

    while (this.match('&')) {
      const right = this.parseAdditive();
      if (isError(left)) return left;
      if (isError(right)) return right;

      // ⚠ Порожнеча в конкатенації — порожній рядок, а не «результат
      // порожній»: `NULL & 'x'` дає `x`. Це єдине місце, де NULL не
      // поглинає вираз, і саме тому воно окремо в наборі еквівалентності.
      left = `${text(left)}${text(right)}`;
    }

    return left;
  }

  private parseAdditive(): FormulaValue {
    let left = this.parseMultiplicative();

    for (;;) {
      const operator = ['+', '-'].find((o) => this.check(o));
      if (operator === undefined) return left;

      this.advance();
      const right = this.parseMultiplicative();
      left = arithmetic(operator, left, right);
    }
  }

  private parseMultiplicative(): FormulaValue {
    let left = this.parseUnary();

    for (;;) {
      const operator = ['*', '/', '%'].find((o) => this.check(o));
      if (operator === undefined) return left;

      this.advance();
      const right = this.parseUnary();
      left = arithmetic(operator, left, right);
    }
  }

  /**
   * Унарний мінус.
   *
   * ⚠ Він **слабший** за степінь: `-2 ^ 2` дає −4, як в Excel і на сервері.
   * Зробити навпаки означало б отримати 4 — правдоподібне число, розбіжність
   * у якому знайшли б на звірці.
   */
  private parseUnary(): FormulaValue {
    if (this.match('-')) {
      const operand = this.parseUnary();

      return arithmetic('-', 0, operand);
    }

    if (this.match('+')) return this.parseUnary();

    return this.parsePower();
  }

  /** Степінь правоасоціативний: `2 ^ 3 ^ 2` = 512, а не 64. */
  private parsePower(): FormulaValue {
    const base = this.parsePrimary();

    if (!this.match('^')) return base;

    const exponent = this.parseUnary();

    return arithmetic('^', base, exponent);
  }

  private parsePrimary(): FormulaValue {
    const token = this.current;

    if (token.kind === 'number') {
      this.advance();

      return Number(token.value);
    }

    if (token.kind === 'text') {
      this.advance();

      return token.value;
    }

    if (this.match('(')) {
      const value = this.parseExpression();
      this.expect(')');

      return value;
    }

    if (token.kind === 'name') {
      this.advance();

      const upper = token.value.toUpperCase();
      if (upper === 'NULL') return null;
      if (upper === 'TRUE') return true;
      if (upper === 'FALSE') return false;

      if (this.match('(')) {
        return call(upper, this.parseArguments());
      }

      // Посилання на комірку в підказці не резолвиться: даних тут немає.
      // #NAME видно як «підказка не змогла», а не як число, якому вірять.
      return Name;
    }

    throw new SyntaxError('Очікувався операнд.');
  }

  private parseArguments(): FormulaValue[] {
    const args: FormulaValue[] = [];

    if (this.match(')')) return args;

    for (;;) {
      args.push(this.parseExpression());

      if (this.match(')')) return args;
      this.expect(',');
    }
  }

  private get current(): Token {
    return this.tokens[this.position] ?? { kind: 'end', value: '' };
  }

  private advance(): void {
    this.position++;
  }

  private check(punct: string): boolean {
    return this.current.kind === 'punct' && this.current.value === punct;
  }

  private match(punct: string): boolean {
    if (!this.check(punct)) return false;
    this.advance();

    return true;
  }

  private matchName(name: string): boolean {
    if (this.current.kind !== 'name' || this.current.value.toUpperCase() !== name) return false;
    this.advance();

    return true;
  }

  private expect(punct: string): void {
    if (!this.match(punct)) throw new SyntaxError(`Очікувалося «${punct}».`);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Семантика
// ─────────────────────────────────────────────────────────────────────────────

function truthy(value: FormulaValue): boolean {
  if (isError(value) || value === null) return false;
  if (typeof value === 'boolean') return value;
  if (typeof value === 'number') return value !== 0;

  return value.length > 0;
}

function text(value: FormulaValue): string {
  if (value === null) return '';
  if (typeof value === 'boolean') return value ? 'true' : 'false';
  if (typeof value === 'number') return formatNumber(value);
  if (isError(value)) return value.error;

  return value;
}

function numeric(value: FormulaValue): number | null | FormulaError {
  if (isError(value) || value === null) return value;
  if (typeof value === 'number') return value;
  if (typeof value === 'boolean') return value ? 1 : 0;

  const parsed = Number(value);

  return Number.isFinite(parsed) ? parsed : Value;
}

/**
 * Арифметика з поглинанням порожнечі.
 *
 * ⚠ `NULL` **поглинає** вираз: `NULL + 1` дає порожнечу, а не 1, і `NULL * 0`
 * теж порожнечу. Це відрізняється від Excel навмисно: у звітності «немає
 * даних» і «нуль» — різні стани, і перетворити перше на друге означає
 * показати нуль там, де вимірювання не проводили.
 *
 * ⛔ Виняток — ділення на порожнечу: воно дає `#DIV/0`, а не порожнечу.
 * Порожній знаменник — це не «немає результату», це неможлива дія.
 */
function arithmetic(operator: string, left: FormulaValue, right: FormulaValue): FormulaValue {
  const a = numeric(left);
  const b = numeric(right);

  if (isError(a)) return a;
  if (isError(b)) return b;

  if ((operator === '/' || operator === '%') && (b === null || b === 0)) return DivideByZero;
  if (a === null || b === null) return null;

  switch (operator) {
    case '+':
      return a + b;
    case '-':
      return a - b;
    case '*':
      return a * b;
    case '/':
      return a / b;

    // Остача бере знак ДІЛЕНОГО, як у C# і на сервері, а не як у Excel:
    // -7 % 4 дає -3, а не 1.
    case '%':
      return a % b;
    case '^':
      return a ** b;
    default:
      return Value;
  }
}

function compare(operator: string, left: FormulaValue, right: FormulaValue): FormulaValue {
  if (isError(left)) return left;
  if (isError(right)) return right;

  // ⚠ Порівняння з порожнечею дає порожнечу, крім рівності: `NULL > 1` — це
  // не «false», це «невідомо». А `NULL = NULL` — true: два невідомі однаково
  // порожні, і саме за цим у звіті шукають незаповнене.
  if (operator === '=' || operator === '<>') {
    const equal = left === null || right === null ? left === right : same(left, right);

    return operator === '=' ? equal : !equal;
  }

  if (left === null || right === null) return null;

  const a = numeric(left);
  const b = numeric(right);
  if (isError(a) || isError(b) || a === null || b === null) return Value;

  switch (operator) {
    case '<':
      return a < b;
    case '<=':
      return a <= b;
    case '>':
      return a > b;
    case '>=':
      return a >= b;
    default:
      return Value;
  }
}

function same(left: FormulaValue, right: FormulaValue): boolean {
  if (typeof left === 'number' || typeof right === 'number') {
    const a = numeric(left);
    const b = numeric(right);

    return typeof a === 'number' && typeof b === 'number' && a === b;
  }

  return left === right;
}

function logical(
  left: FormulaValue,
  right: FormulaValue,
  combine: (a: boolean, b: boolean) => boolean,
): FormulaValue {
  if (isError(left)) return left;
  if (isError(right)) return right;
  if (left === null || right === null) return null;

  return combine(truthy(left), truthy(right));
}

/** Числові аргументи функції: порожнечі **пропускаються**, помилки — ні. */
function numbers(args: FormulaValue[]): number[] | FormulaError {
  const result: number[] = [];

  for (const arg of args) {
    const value = numeric(arg);
    if (isError(value)) return value;
    if (value === null) continue;

    result.push(value);
  }

  return result;
}

/**
 * Округлення **від нуля** на півдорозі: `ROUND(2.5, 0)` = 3, `ROUND(-2.5, 0)`
 * = −3.
 *
 * ⛔ `Math.round` не годиться: він округлює −2.5 до −2 (у бік плюс
 * нескінченності). Сервер рахує `MidpointRounding.AwayFromZero`, і півкопійки
 * в тисячі рядків дають розбіжність, яку неможливо пояснити.
 */
function round(value: number, digits: number): number {
  const factor = 10 ** digits;
  const scaled = value * factor;

  // Компенсація двійкового дробу: 1.005 * 100 у IEEE-754 дає 100.49999…,
  // і без цього ROUND(1.005, 2) повернув би 1 замість 1.01.
  const corrected = Number.parseFloat(scaled.toPrecision(15));

  return (corrected < 0 ? -Math.round(-corrected) : Math.round(corrected)) / factor;
}

function call(name: string, args: FormulaValue[]): FormulaValue {
  const first = args.find(isError);
  const values = (): number[] | FormulaError => numbers(args);

  switch (name) {
    case 'SUM': {
      if (first !== undefined) return first;
      const list = values();

      return isError(list) ? list : list.reduce((sum, n) => sum + n, 0);
    }

    case 'PRODUCT': {
      if (first !== undefined) return first;
      const list = values();

      return isError(list) ? list : list.reduce((product, n) => product * n, 1);
    }

    case 'AVERAGE': {
      if (first !== undefined) return first;
      const list = values();
      if (isError(list)) return list;

      // ⚠ Середнє порожньої множини — ПОРОЖНЕЧА, а не нуль і не помилка:
      // «середнє ні з чого» не існує, а нуль тут читався б як вимірювання.
      return list.length === 0 ? null : list.reduce((sum, n) => sum + n, 0) / list.length;
    }

    case 'MIN':
    case 'MAX': {
      if (first !== undefined) return first;
      const list = values();
      if (isError(list)) return list;
      if (list.length === 0) return null;

      return name === 'MIN' ? Math.min(...list) : Math.max(...list);
    }

    case 'COUNT': {
      if (first !== undefined) return first;
      const list = values();

      return isError(list) ? list : list.length;
    }

    case 'ROUND': {
      if (first !== undefined) return first;
      const list = values();
      if (isError(list)) return list;
      if (list.length === 0) return null;

      return round(list[0] ?? 0, Math.trunc(list[1] ?? 0));
    }

    case 'ABS': {
      if (first !== undefined) return first;
      const list = values();
      if (isError(list)) return list;

      return list.length === 0 ? null : Math.abs(list[0] ?? 0);
    }

    case 'IF': {
      const condition = args[0];
      if (condition !== undefined && isError(condition)) return condition;

      return truthy(condition ?? null) ? (args[1] ?? null) : (args[2] ?? null);
    }

    // ⚠ IFERROR ловить ЛИШЕ помилку. Порожнеча крізь нього проходить: «немає
    // даних» — не збій, і замінювати її запасним числом означало б вигадати
    // вимірювання.
    case 'IFERROR':
      return isError(args[0] ?? null) ? (args[1] ?? null) : (args[0] ?? null);

    case 'SUMIF': {
      const value = args[0] ?? null;
      const condition = args[1] ?? null;
      if (isError(value)) return value;
      if (isError(condition)) return condition;

      const number = numeric(value);
      if (isError(number)) return number;

      return truthy(condition) ? (number ?? 0) : 0;
    }

    default:
      return Name;
  }
}
