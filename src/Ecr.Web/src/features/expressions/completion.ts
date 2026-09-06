import type {
  ExpressionFunctionDto,
  ExpressionMetadataDto,
  ExpressionSymbolDto,
} from '@/api/types';

/**
 * Що саме доповнюється в цій позиції — і чим.
 *
 * ⛔ Чиста функція, окремо від Monaco, і не заради чистоти. Рендер важкого
 * компонента в jsdom іде хвилинами (`D1-12`), і перевірка автодоповнення через
 * редактор була б тестом, який вимкнули б першим. Тут перевіряється рівно те,
 * що ламається: ЯКИЙ префікс розпізнано і ЩО він пропонує.
 */

/** Вид символу, який доповнюється. */
export type CompletionKind =
  | 'constant'
  | 'formula'
  | 'argument'
  | 'header'
  | 'function';

/** Один варіант підстановки. */
export interface CompletionItem {
  /** Що підставити. */
  readonly insert: string;
  /** Що показати в переліку. */
  readonly label: string;
  /** Вид — визначає піктограму і сортування. */
  readonly kind: CompletionKind;
  /** Права колонка переліку: одиниця, тип результату. */
  readonly detail?: string | undefined;
  /** Пояснення під переліком. */
  readonly documentation?: string | undefined;
}

/** Що доповнюємо і який фрагмент тексту замінюємо. */
export interface CompletionContext {
  /** Розпізнаний вид. */
  readonly kind: CompletionKind;
  /** Початок фрагмента, який заміняє підстановка (зміщення в символах). */
  readonly replaceFrom: number;
  /** Уже набрана частина імені — для фільтрації. */
  readonly typed: string;
}

/**
 * Префікси мови (`02b` §3.4) у порядку перевірки.
 *
 * ⚠ `CST.` і `HDR.` — ПЕРЕД однолітерними: інакше `!` та `@` ніколи не дійшли
 * б до перевірки в тексті на кшталт `CST.X`, бо крапка вже все вирішила.
 */
const Prefixes: readonly { readonly text: string; readonly kind: CompletionKind }[] = [
  { text: 'CST.', kind: 'constant' },
  { text: 'HDR.', kind: 'header' },
  { text: '!', kind: 'formula' },
  { text: '@', kind: 'argument' },
];

/**
 * Визначає, що доповнюється зліва від курсора.
 *
 * @param text Увесь текст виразу.
 * @param offset Позиція курсора — зміщення в символах від початку.
 * @returns Контекст доповнення; `null` — доповнювати нема чого.
 */
export function completionAt(text: string, offset: number): CompletionContext | null {
  const before = text.slice(0, Math.max(0, Math.min(offset, text.length)));

  // Ім'я, яке вже набрали: літери, цифри і підкреслення.
  const typed = /[A-Za-z0-9_]*$/.exec(before)?.[0] ?? '';
  const head = before.slice(0, before.length - typed.length);

  for (const { text: prefix, kind } of Prefixes) {
    if (head.endsWith(prefix)) {
      return { kind, replaceFrom: before.length - typed.length, typed };
    }
  }

  // ⛔ Усередині рядкового літерала не доповнюється НІЧОГО. `'@Fuel'` — це
  // текст, а не посилання, і підставити туди ім'я аргументу означало б мовчки
  // зіпсувати константу, яку користувач саме друкує.
  if (insideString(before)) {
    return null;
  }

  // ⚠ Порожній `typed` теж доповнюється: перелік функцій має відкриватися по
  // Ctrl+Space на порожньому місці, а не лише після першої літери.
  return { kind: 'function', replaceFrom: before.length - typed.length, typed };
}

/**
 * Чи стоїть курсор усередині рядкового літерала.
 *
 * ⚠ Рахуються лапки з урахуванням подвоєння `''` — єдиного способу екранувати
 * лапку в нашій мові (`02b` §1). Наївний підрахунок непарності зламався б на
 * `'don''t'` і вважав би решту виразу текстом.
 */
function insideString(before: string): boolean {
  let inside = false;

  for (let i = 0; i < before.length; i++) {
    if (before[i] !== "'") continue;

    if (inside && before[i + 1] === "'") {
      i++;
      continue;
    }

    inside = !inside;
  }

  return inside;
}

/**
 * Варіанти підстановки для контексту.
 *
 * @param context Що доповнюємо.
 * @param metadata Склад мови, отриманий із сервера.
 * @returns Відфільтрований і впорядкований перелік.
 */
export function completionsFor(
  context: CompletionContext,
  metadata: ExpressionMetadataDto | undefined,
): readonly CompletionItem[] {
  if (metadata === undefined) return [];

  const all =
    context.kind === 'function'
      ? metadata.functions.map(functionItem)
      : symbolsOf(metadata, context.kind).map((s) => symbolItem(s, context.kind));

  const typed = context.typed.toUpperCase();

  return all
    .filter((item) => item.insert.toUpperCase().startsWith(typed))
    .sort((a, b) => a.label.localeCompare(b.label));
}

function symbolsOf(
  metadata: ExpressionMetadataDto,
  kind: CompletionKind,
): readonly ExpressionSymbolDto[] {
  switch (kind) {
    case 'constant':
      return metadata.constants;
    case 'formula':
      return metadata.formulas;
    case 'argument':
      return metadata.arguments;
    case 'header':
      return metadata.headers;
    default:
      return [];
  }
}

function symbolItem(symbol: ExpressionSymbolDto, kind: CompletionKind): CompletionItem {
  return {
    insert: symbol.name,
    label: symbol.name,
    kind,
    detail: symbol.unit ?? undefined,
    documentation: symbol.note ?? undefined,
  };
}

/**
 * Варіант для функції: підставляє дужки і лишає курсор між ними.
 *
 * ⚠ Функція без аргументів у нашій мові не існує — усі 24 приймають
 * щонайменше один (`02b` §7–§8). Тому дужки підставляються завжди, і курсор
 * завжди всередині: інакше кожен виклик доводилося б дописувати руками.
 */
function functionItem(fn: ExpressionFunctionDto): CompletionItem {
  return {
    insert: fn.name,
    label: fn.name,
    kind: 'function',
    detail: signatureOf(fn),
    documentation: fn.resultType ?? undefined,
  };
}

/**
 * Людський вигляд сигнатури: `SUM(діапазон | число, …)`.
 *
 * ⛔ Показує саме те, що перевірить публікація: скільки аргументів і чи
 * приймається діапазон. Підказка, яка про діапазон мовчить, навчала б
 * передавати комірки по одній — а це і є та форма, від якої відходить уся
 * міграція з Excel.
 */
export function signatureOf(fn: ExpressionFunctionDto): string {
  const parts: string[] = [];

  for (let i = 0; i < fn.minArgs; i++) {
    parts.push(fn.acceptsRange && i === 0 ? 'range | number' : `arg${i + 1}`);
  }

  if (fn.maxArgs === null || fn.maxArgs === undefined) {
    parts.push('…');
  } else if (fn.maxArgs > fn.minArgs) {
    parts.push(`arg${fn.minArgs + 1}?`);
  }

  return `${fn.name}(${parts.join(', ')})`;
}
