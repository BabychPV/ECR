/**
 * Параметри звіту `@Name` на стороні клієнта (`R6`, `02b` §8a).
 *
 * Оголошення живуть у `rulesJson` ВЕРСІЇ опису звіту (схема 2), значення
 * задаються при побудові зрізу і йдуть у тілі `POST /reports/{code}/build`.
 * Сервер зводить їх з оголошеннями ще в обробнику запиту, тож невідоме ім'я,
 * обов'язковий параметр без значення й без `default` і значення не того типу
 * дають `422 ECR-RPT-0422` одразу у відповіді — а не невдалою задачею через
 * хвилину.
 *
 * ⛔ Через це розбір тут — не прикраса форми, а деградація В БІК ЗАБОРОНИ
 * (директива `D15` §0, `L10`). Розбір має ТРИ результати, і плутати два
 * останні не можна:
 *   - `unreadable` — оголошення прочитати не вдалося. Побудова недоступна:
 *     наосліп вона або впаде `422`, або — гірше — пройде без параметра й дасть
 *     зріз, який виглядає нормальним. «Не знаємо — не пускаємо»;
 *   - `declared` з порожнім переліком — параметрів СПРАВДІ немає (схема 1 не
 *     має поля `parameters` взагалі). Побудова працює як раніше;
 *   - `declared` з переліком — малюємо поля.
 *
 * ⚠ `rulesJson` — рядок із сервера, а не наш об'єкт: він може бути невалідним
 * JSON, і тоді `JSON.parse` кидає. Мовчазне `catch → []` перетворило б
 * «прочитати не вдалося» на «параметрів немає» — тобто саме на той третій
 * стан, який дозволяє побудову наосліп.
 */

/** Тип оголошеного параметра. Той самий словник, що приймає сервер. */
export type ReportParameterType = 'Number' | 'Text' | 'Boolean' | 'Date';

/** Оголошення параметра у версії опису звіту (`ReportParameterCommand`). */
export interface ReportParameterDeclaration {
  /** Ім'я без `@`; у виразі правила — `@Code`. */
  readonly code: string;
  readonly type: ReportParameterType;
  readonly required: boolean;
  /**
   * Замовчування, яке візьме побудова, коли значення не передане.
   *
   * ⚠ `undefined` І `null` означають «замовчування немає»: сервер читає явний
   * `null` як «не передано», тож обіцяти ним значення було б неправдою.
   */
  readonly defaultValue: unknown;
}

/** Значення параметра у чернетці форми. */
export type ParameterValue = string | number | boolean | Date | null;

/** Чернетка значень за іменем параметра. */
export type ParameterDraft = Readonly<Record<string, ParameterValue>>;

/** Результат розбору оголошень — рівно три стани, див. заголовок файлу. */
export type ParameterDeclarations =
  | { readonly kind: 'declared'; readonly items: readonly ReportParameterDeclaration[] }
  | { readonly kind: 'unreadable' };

/** Оголошення прочитати не вдалося. */
export const UnreadableParameters: ParameterDeclarations = { kind: 'unreadable' };

/** Параметрів немає — і це ЗНАННЯ, а не брак знання. */
export const NoParameters: ParameterDeclarations = { kind: 'declared', items: [] };

/**
 * Відомі типи за написанням у JSON.
 *
 * ⚠ Ключі в нижньому регістрі: сервер порівнює тип без урахування регістру
 * (`ReportParameterCommand.type`), і клієнт, суворіший за сервер, блокував би
 * побудову за описом, який сервер приймає.
 */
const KnownTypes: Readonly<Record<string, ReportParameterType>> = {
  number: 'Number',
  text: 'Text',
  boolean: 'Boolean',
  date: 'Date',
};

/** Розбирає `rulesJson` версії опису звіту в оголошення параметрів. */
export function readReportParameters(rulesJson: string | null | undefined): ParameterDeclarations {
  // ⛔ Рядка немає зовсім (версії не знайшли, перелік описів не приїхав) — це
  // «не знаємо», а не «параметрів немає».
  if (typeof rulesJson !== 'string' || rulesJson.trim().length === 0) {
    return UnreadableParameters;
  }

  let parsed: unknown;

  try {
    parsed = JSON.parse(rulesJson);
  } catch {
    return UnreadableParameters;
  }

  /*
   * ⚠ Розібралося, але це не об'єкт правил (масив, число, рядок) — «параметрів
   * немає», а НЕ «не знаємо». Так читає його сам сервер: `ReportRules.Parse`
   * ловить `JsonException`, бере правила за замовчуванням, і `ReportParameters.Of`
   * повертає порожній перелік. Клієнт, суворіший за сервер, блокував би побудову
   * за описом, який сервер будує без жодних питань — зокрема за `rulesJson: '[]'`
   * із фікстури `SnapshotsPage.refreshAfterBuild.test.tsx`.
   *
   * ⛔ Випадок, який СПРАВДІ «не знаємо», інший і лишився вище: `JSON.parse`
   * кинув. Там байти зіпсовані, і секція `parameters` могла бути серед них.
   */
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    return NoParameters;
  }

  const raw = (parsed as { parameters?: unknown }).parameters;

  // ⚠ У JSON схеми 1 поля `parameters` немає взагалі — це не помилка.
  if (raw === undefined || raw === null) return NoParameters;

  if (!Array.isArray(raw)) return UnreadableParameters;

  const items: ReportParameterDeclaration[] = [];

  for (const entry of raw) {
    const declaration = readDeclaration(entry);

    // ⛔ Один нерозібраний елемент робить нечитабельним УВЕСЬ перелік:
    // пропустити його означало б показати форму, у якій не вистачає поля, —
    // і побудова пішла б без параметра, про який ми навіть не сказали.
    if (declaration === null) return UnreadableParameters;

    items.push(declaration);
  }

  return { kind: 'declared', items };
}

function readDeclaration(entry: unknown): ReportParameterDeclaration | null {
  if (typeof entry !== 'object' || entry === null || Array.isArray(entry)) return null;

  const record = entry as Record<string, unknown>;
  const code = record['code'];

  if (typeof code !== 'string' || code.trim().length === 0) return null;

  const written = record['type'];

  if (typeof written !== 'string') return null;

  const type = KnownTypes[written.toLowerCase()];

  if (type === undefined) return null;

  return {
    code,
    type,
    required: record['required'] === true,
    defaultValue: record['default'],
  };
}

/** Початкова чернетка: замовчування підставлені, решта — порожньо. */
export function defaultDraft(items: readonly ReportParameterDeclaration[]): ParameterDraft {
  const draft: Record<string, ParameterValue> = {};

  for (const item of items) draft[item.code] = initialValue(item);

  return draft;
}

function initialValue(declaration: ReportParameterDeclaration): ParameterValue {
  const fallback = declaration.defaultValue;

  switch (declaration.type) {
    // ⚠ Тумблер не має стану «не обрано»: без замовчування це `false`, і це
    // справжнє значення, а не порожнеча. Тому обов'язкове булеве ніколи не
    // блокує побудову — ми завжди надсилаємо те, що людина бачить на екрані.
    case 'Boolean':
      return typeof fallback === 'boolean' ? fallback : false;
    case 'Number':
      return typeof fallback === 'number' ? fallback : '';
    case 'Date':
      return parseDay(fallback);
    default:
      return typeof fallback === 'string' ? fallback : '';
  }
}

/** Чи значення в чернетці заповнене для цього оголошення. */
export function isFilled(declaration: ReportParameterDeclaration, value: ParameterValue): boolean {
  switch (declaration.type) {
    case 'Boolean':
      return typeof value === 'boolean';
    case 'Number':
      return typeof value === 'number' && Number.isFinite(value);
    case 'Date':
      return value instanceof Date;
    default:
      return typeof value === 'string' && value.trim().length > 0;
  }
}

/**
 * Імена обов'язкових параметрів, які побудувати не дадуть: без значення і без
 * замовчування.
 *
 * ⚠ Обов'язковий параметр із замовчуванням — законна пара: замовчування і є
 * тим значенням, без якого не будують, і сервер підставить його сам.
 */
export function missingRequired(
  items: readonly ReportParameterDeclaration[],
  draft: ParameterDraft,
): string[] {
  return items
    .filter(
      (item) =>
        item.required &&
        (item.defaultValue === undefined || item.defaultValue === null) &&
        !isFilled(item, draft[item.code] ?? null),
    )
    .map((item) => item.code);
}

/**
 * Тіло `parameters` запиту побудови: значення ПОТРІБНИХ типів за іменем.
 *
 * ⚠ `undefined` — коли параметрів немає зовсім: поля `parameters` у тілі тоді
 * не з'являється, і запит лишається побайтно таким, яким був до `R6`.
 *
 * ⚠ Приведення робить КЛІЄНТ, бо сервер його не робить навмисно: рядок `"5"`
 * у параметр `Number` — відмова, а не число.
 */
export function toParametersBody(
  items: readonly ReportParameterDeclaration[],
  draft: ParameterDraft,
): Record<string, unknown> | undefined {
  if (items.length === 0) return undefined;

  const body: Record<string, unknown> = {};

  for (const item of items) {
    const value = coerce(item, draft[item.code] ?? null);

    // ⚠ Незаповнене не надсилається зовсім: явний `null` сервер читає як «не
    // передано», але тоді ми б самі стерли замовчування, підставивши порожнечу.
    if (value !== undefined) body[item.code] = value;
  }

  return body;
}

function coerce(declaration: ReportParameterDeclaration, value: ParameterValue): unknown {
  if (!isFilled(declaration, value)) return undefined;

  if (declaration.type === 'Date') return value instanceof Date ? formatDay(value) : undefined;

  return value;
}

/**
 * Дата як `YYYY-MM-DD` за МІСЦЕВИМИ складниками.
 *
 * ⛔ Не `toISOString()`: той переводить у UTC і зсуває дату на добу для всіх,
 * хто живе західніше за Гринвіч, — тобто перше березня поїхало б у запит як
 * двадцять восьме лютого. Складники дати (на відміну від складників часу доби)
 * для машинного формату брати руками дозволено навмисно (`D15-09`).
 */
function formatDay(value: Date): string {
  const pad = (part: number): string => String(part).padStart(2, '0');

  return `${String(value.getFullYear()).padStart(4, '0')}-${pad(value.getMonth() + 1)}-${pad(value.getDate())}`;
}

/**
 * `YYYY-MM-DD` із замовчування — у МІСЦЕВУ дату.
 *
 * ⛔ Дзеркальне до `formatDay` і з тієї ж причини: `new Date('2026-03-01')`
 * читається як опівніч UTC, і `formatDay` повернув би з неї інший день.
 */
export function parseDay(raw: unknown): Date | null {
  if (typeof raw !== 'string') return null;

  const text = raw.trim();

  if (!/^\d{4}-\d{2}-\d{2}/.test(text)) return null;

  const year = Number(text.slice(0, 4));
  const month = Number(text.slice(5, 7));
  const day = Number(text.slice(8, 10));
  const value = new Date(year, month - 1, day);

  // ⚠ Перевірка складниками, а не `isNaN`: `new Date(2026, 1, 31)` не «погана
  // дата», а мовчки третє березня — тобто запис `2026-02-31` повернувся б із
  // форми іншим днем, ніж прийшов.
  if (value.getFullYear() !== year || value.getMonth() !== month - 1 || value.getDate() !== day) {
    return null;
  }

  return value;
}
