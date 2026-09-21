/**
 * Перевірка виразу cron ДО збереження (формат Quartz, `CronExpression`).
 *
 * ⛔ Авторитетна перевірка — серверна (`ListCollectionSchedulesHandler
 * .RequireValidCron` → `QuartzJobScheduler.IsValidCron`). Ця лише не дає
 * формі пообіцяти збереження, яке сервер однаково відхилить: кнопка
 * недоступна, а причина видима до кліку.
 *
 * ⚠ Напрям похибки обраний свідомо: СУВОРІШЕ за Quartz, ніколи не м'якше.
 * Кожне правило нижче звірене з `Quartz.CronExpression.ValidateExpression`
 * 3.13.1 (та версія, що в `Directory.Packages.props`) на наборі з ~130
 * виразів. Quartz приймає і дивні форми — `1,` (порожній елемент списку),
 * `*\/0`, `FEBR`, восьме поле-сміття, `6L-7`, `1-5W`; форма їх відхиляє, бо
 * пообіцяти те, що ми не звірили, дорожче, ніж попросити канонічний запис.
 * Відхилити валідне ми можемо; пропустити невалідне — ні.
 */

/** Межа довжини — `CollectionSchedule.MaxCronLength`. */
export const MaxCronLength = 100;

/** Причина відмови: ключ каталогу й параметри для `t()`. */
export interface CronProblem {
  readonly key: string;
  readonly params?: Record<string, string | number>;
}

interface FieldSpec {
  readonly min: number;
  readonly max: number;
  /** Найбільший крок `/n`, який Quartz ще приймає (`Increment > N`). */
  readonly maxStep: number;
  readonly names?: readonly string[];
}

const Months = ['JAN', 'FEB', 'MAR', 'APR', 'MAY', 'JUN', 'JUL', 'AUG', 'SEP', 'OCT', 'NOV', 'DEC'];
const Days = ['SUN', 'MON', 'TUE', 'WED', 'THU', 'FRI', 'SAT'];

const Seconds: FieldSpec = { min: 0, max: 59, maxStep: 59 };
const Minutes: FieldSpec = { min: 0, max: 59, maxStep: 59 };
const Hours: FieldSpec = { min: 0, max: 23, maxStep: 23 };
const DayOfMonth: FieldSpec = { min: 1, max: 31, maxStep: 31 };
const Month: FieldSpec = { min: 1, max: 12, maxStep: 12, names: Months };
const DayOfWeek: FieldSpec = { min: 1, max: 7, maxStep: 7, names: Days };

/*
 * ⚠ Роки 1970–2099 — вужче, ніж приймає Quartz (1969 і 2300 він пропускає):
 * це документований діапазон формату, і ширший тут нічого не дає.
 */
const Year: FieldSpec = { min: 1970, max: 2099, maxStep: 99 };

const Specs = [Seconds, Minutes, Hours, DayOfMonth, Month, DayOfWeek, Year] as const;

const DomIndex = 3;
const DowIndex = 5;

/** Значення поля: число в межах або назва (`JAN`, `MON`). `null` — ні те, ні інше. */
function valueOf(token: string, spec: FieldSpec): number | null {
  if (/^\d{1,4}$/.test(token)) {
    const n = Number(token);

    return n >= spec.min && n <= spec.max ? n : null;
  }

  const index = spec.names?.indexOf(token) ?? -1;

  return index < 0 ? null : index + spec.min;
}

function isNumber(token: string): boolean {
  return /^\d{1,4}$/.test(token);
}

/**
 * Один елемент списку: `*`, `n`, `a-b`, кожен із необов'язковим `/крок`.
 *
 * ⚠ Діапазон `a-b` з `a > b` дозволений (`22-2` годин, `DEC-JAN`) — Quartz
 * розуміє його як перехід через межу. Виняток — рік: там `Start year must be
 * less than stop year`.
 *
 * ⚠ Діапазон з назвою й числом (`JAN-12`, `MON-2`) Quartz відхиляє; назва з
 * кроком не звірена — тож теж ні.
 */
function isValidItem(item: string, spec: FieldSpec, isYear: boolean): boolean {
  const [base = '', step, extra] = item.split('/');

  if (extra !== undefined) return false;

  if (step !== undefined) {
    if (!isNumber(step)) return false;

    const n = Number(step);

    if (n < 1 || n > spec.maxStep) return false;
  }

  if (base === '*') return true;

  const [from = '', to, tail] = base.split('-');

  if (tail !== undefined) return false;

  if (to === undefined) {
    if (step !== undefined && !isNumber(from)) return false;

    return valueOf(from, spec) !== null;
  }

  // Обидва кінці — одного роду: або числа, або назви.
  if (isNumber(from) !== isNumber(to)) return false;
  if (step !== undefined && !isNumber(from)) return false;

  const a = valueOf(from, spec);
  const b = valueOf(to, spec);

  if (a === null || b === null) return false;

  return isYear ? a < b : true;
}

function isValidList(field: string, spec: FieldSpec, isYear = false): boolean {
  return field.split(',').every((item) => item.length > 0 && isValidItem(item, spec, isYear));
}

/**
 * Особливі форми дня місяця — лише ЦІЛИМ полем: `L`, `L-n` (n ≤ 30), `LW`,
 * `nW`. У списку з іншими днями Quartz `L` не підтримує, а `W` у списку не
 * звірене.
 */
function isValidDayOfMonth(field: string): boolean {
  if (field === 'L' || field === 'LW') return true;

  const offset = /^L-(\d{1,2})$/.exec(field);
  if (offset !== null) return Number(offset[1]) <= 30;

  const weekday = /^(\d{1,2})W$/.exec(field);
  if (weekday !== null) return valueOf(weekday[1] ?? '', DayOfMonth) !== null;

  return isValidList(field, DayOfMonth);
}

/**
 * Особливі форми дня тижня — лише ЦІЛИМ полем: `L`, `nL`, `n#k` (k 1–5), де
 * `n` — число 1–7 або назва. Кілька «n-их» днів і `L` поряд з іншими днями
 * Quartz не підтримує.
 */
function isValidDayOfWeek(field: string): boolean {
  if (field === 'L') return true;

  const last = /^([A-Z]{3}|\d)L$/.exec(field);
  if (last !== null) return valueOf(last[1] ?? '', DayOfWeek) !== null;

  const nth = /^([A-Z]{3}|\d)#(\d)$/.exec(field);
  if (nth !== null) {
    const k = Number(nth[2]);

    return valueOf(nth[1] ?? '', DayOfWeek) !== null && k >= 1 && k <= 5;
  }

  return isValidList(field, DayOfWeek);
}

function isValidField(index: number, field: string): boolean {
  if (index === DomIndex) return isValidDayOfMonth(field);
  if (index === DowIndex) return isValidDayOfWeek(field);

  const spec = Specs[index];

  return spec !== undefined && isValidList(field, spec, index === 6);
}

/**
 * Перевіряє вираз; `null` — вираз прийнятний, інакше — причина.
 *
 * ⚠ Порядок перевірок той самий, що на сервері: спершу довжина, потім
 * синтаксис. Причина — ПЕРША знайдена, з номером поля: «щось не так» не
 * підказує, що виправити.
 */
export function checkCron(input: string): CronProblem | null {
  const text = input.trim();

  if (text.length === 0) return { key: 'schedule.cronEmpty' };

  if (text.length > MaxCronLength) {
    return { key: 'schedule.cronTooLong', params: { max: MaxCronLength } };
  }

  // Quartz не зважає на регістр: `mon-fri` — те саме, що `MON-FRI`.
  const fields = text.toUpperCase().split(/\s+/);

  if (fields.length < 6 || fields.length > 7) {
    return { key: 'schedule.cronFieldCount', params: { count: fields.length } };
  }

  // ⛔ Рівно одне з полів дня — `?`. Обидва конкретні — «not implemented» у
  // Quartz; обидва `?` — теж відмова.
  const domAsked = fields[DomIndex] === '?';
  const dowAsked = fields[DowIndex] === '?';

  if (domAsked === dowAsked) return { key: 'schedule.cronDayQuestion' };

  for (const [index, field] of fields.entries()) {
    if ((index === DomIndex && domAsked) || (index === DowIndex && dowAsked)) continue;

    if (!isValidField(index, field)) {
      return {
        key: 'schedule.cronField',
        params: { position: index + 1, value: text.split(/\s+/)[index] ?? field },
      };
    }
  }

  return null;
}
