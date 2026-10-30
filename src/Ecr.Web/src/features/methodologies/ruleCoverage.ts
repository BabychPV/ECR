import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/*
 * ⛔ Адреса записана повністю, а не збирається з помічника — з тієї самої
 * причини, що й у `api.ts` (коментар там): сторож
 * `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає в коді клієнта саме
 * літерал `/api/v1/…` разом із методом поруч.
 *
 * ⚠ Окремий файл, а не рядок у `api.ts`: матриця має власні типи вікна й
 * власні чисті функції (`defaultRuleCoverageWindow`, `ruleCoverageWindowEmpty`),
 * які тестуються без мережі.
 */

/** Матриця покриття «рядки реальних даних × правила» (`ФВ-13.9`). */
export type RuleCoverageDto = components['schemas']['RuleCoverageDto'];

/** Одна комбінація значень і що з нею роблять правила. */
export type RuleCoverageCombinationDto = components['schemas']['RuleCoverageCombinationDto'];

/** Стан комбінації: розрив, конфлікт, покрито. */
export type RuleCoverageState = components['schemas']['RuleCoverageState'];

/**
 * Вікно запиту: яка таблиця й за які періоди.
 *
 * ⚠ `tableDefId: null` — це НЕ «нічого не обрано», а «усі таблиці прив'язок»:
 * саме так сервер трактує відсутній параметр (`RuleCoverageHandler`).
 */
interface RuleCoverageWindow {
  readonly tableDefId: number | null;
  readonly periodFrom: number;
  readonly periodTo: number;
}

/**
 * Вікно за замовчуванням — минулий і поточний календарні роки.
 *
 * ⛔ Вікно ЗАВЖДИ передається серверу явно, тож у полях стоїть саме те вікно, за
 * яке прийде відповідь. Порожні поля з «сервер щось підставить» означали б, що
 * людина не знає, за який період дивиться матрицю.
 *
 * ✎ 2026-10-30 (lane/integ-methodology-ui): верхня межа — `YYYY12`, а не серверне
 * `YYYY99`. Поля тепер — `PeriodPicker` (той самий, що на решті екранів), а він
 * приймає лише дійсний `periodKey` (`01..12`); `YYYY99` він показати не може. Для
 * місяців, кварталів (`01..04`) і року (`01`) межа `12` охоплює той самий набір
 * ключів; ширше вікно `Custom`-періодичності (номер > 12) — лише набором вручну.
 */
export function defaultRuleCoverageWindow(now: Date): RuleCoverageWindow {
  const year = now.getFullYear();

  return { tableDefId: null, periodFrom: (year - 1) * 100 + 1, periodTo: year * 100 + 12 };
}

/** Що не так із вікном: `missing` — бракує межі, `reversed` — нижня вища за верхню. */
export type RuleCoverageWindowProblem = 'missing' | 'reversed';

/**
 * Перевірка вікна перед запитом: обидві межі задані й `from ≤ to`.
 *
 * ⚠ Порожня межа — окрема причина, а не «порожнє вікно»: `PeriodPicker` дає
 * `null`, коли поле очищено, і сервер на запит без межі підставив би власну —
 * тобто матриця показала б не той період, що в полях.
 */
export function ruleCoverageWindowProblem(
  periodFrom: number | null,
  periodTo: number | null,
): RuleCoverageWindowProblem | null {
  if (periodFrom === null || periodTo === null) return 'missing';

  return ruleCoverageWindowEmpty(periodFrom, periodTo) ? 'reversed' : null;
}

/**
 * Чи вікно порожнє (нижня межа вища за верхню).
 *
 * ⚠ Та сама умова, що й на сервері (`ECR-CALC-0422`,
 * `err.ECR-CALC-0422.coverageWindow`). Перевірка тут не ЗАМІНЮЄ серверну —
 * вона прибирає свідомо марний запит і називає причину одразу, замість
 * показати її після зайвого кола до сервера.
 */
export function ruleCoverageWindowEmpty(periodFrom: number, periodTo: number): boolean {
  return periodFrom > periodTo;
}

/**
 * Значення комірки комбінації.
 *
 * ⚠ У `schema.d.ts` елемент масиву — `string`, але контракт каже, що `null`
 * означає «комірки немає», і сервер його надсилає. Тип генератора тут вужчий
 * за правду, тож нормалізація стоїть в одному місці, а не в розмітці.
 */
export function ruleCoverageCell(value: string | null | undefined): string | null {
  return value === null || value === undefined || value === '' ? null : value;
}

/**
 * Правила, затінені переможцем (нижчий пріоритет) або суперники в конфлікті.
 *
 * ⛔ Переможець ВИКЛЮЧАЄТЬСЯ зі списку, а не позначається в ньому: показати
 * `matchedRuleCodes` як однорідний перелік означало б стерти єдине, що
 * відповідає на питання «яке правило рахує цей рядок».
 */
export function ruleCoverageOtherRules(
  combination: RuleCoverageCombinationDto,
): readonly string[] {
  return combination.matchedRuleCodes.filter((code) => code !== combination.winnerRuleCode);
}

/** Матриця покриття версії за вікном. Право `Calculation.View`. */
export function ruleCoverage(
  methodologyId: number,
  versionId: number,
  window: RuleCoverageWindow,
): Promise<RuleCoverageDto> {
  const query = new URLSearchParams();

  if (window.tableDefId !== null) query.set('tableDefId', String(window.tableDefId));
  query.set('periodFrom', String(window.periodFrom));
  query.set('periodTo', String(window.periodTo));

  return apiFetch<RuleCoverageDto>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/rule-coverage?${query.toString()}`,
  );
}
