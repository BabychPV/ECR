import type { MethodologyConstantDto } from '@/api/types';
import { t } from '@/shared/i18n';
import type { MethodologyCategoryDto } from './api';

/**
 * Категорії констант версії на клієнті (план CONST-CATEGORY) — рішення, що не є розміткою.
 *
 * ⛔ Категорія константи — ключ зіставлення: правило категорії повертає код, і константа з точно цим
 * кодом (з регістром) перекриває спільну. Описка в категорії доти тихо робила константу мертвою, тому
 * в діалозі категорія — вибір із переліку версії, а не вільний текст.
 */

/** Значення фільтра «лише спільні» в адресі (`?ccat=~shared`); код категорії з `~` не починається на практиці. */
export const SharedFilterValue = '~shared';

/** Параметр адреси фільтра категорії таблиці констант. */
export const CategoryFilterParam = 'ccat';

/** Параметр адреси відкритого діалогу категорій (`?mcats=1`). */
export const CategoriesDialogParam = 'mcats';

/**
 * Рядки таблиці констант під фільтром категорії.
 *
 * ⚠ `null` — без фільтра; {@link SharedFilterValue} — лише спільні (без категорії); інакше — точний код.
 * Фільтр на клієнті: GET `…/constants` без параметрів віддає всю версію, і таблиця її вже тримає.
 */
export function filterConstantsByCategory(
  list: readonly MethodologyConstantDto[],
  filter: string | null,
): MethodologyConstantDto[] {
  if (filter === null || filter === '') return [...list];
  if (filter === SharedFilterValue) return list.filter((c) => c.category === null || c.category === '');
  return list.filter((c) => c.category === filter);
}

/** Підпис категорії в комірці: порожня — «Shared», а не тире (порожня категорія — це рішення, не відсутність). */
export function constantCategoryLabel(category: string | null | undefined): string {
  return category === null || category === undefined || category === ''
    ? t('methodologies.categoryShared')
    : category;
}

/** Варіант вибору. */
export interface CategoryOption {
  readonly value: string;
  readonly label: string;
}

/**
 * Варіанти вибору категорії константи.
 *
 * ⛔ Виведена з ужитку категорія НЕ пропонується новій константі (сервер відповість `422
 * constantCategoryRetired`), але ПОТОЧНЕ значення константи лишається у варіантах із позначкою — інакше
 * поле показало б порожнечу, і збереження мовчки зробило б константу спільною.
 *
 * ⚠ Поточне значення, якого в переліку немає зовсім (дані до плану), так само лишається видимим — сервер
 * відхилить збереження `422 constantCategoryUnknown`, і людина побачить причину, а не зниклу категорію.
 */
export function categoryOptions(
  categories: readonly MethodologyCategoryDto[],
  current: string,
): CategoryOption[] {
  const sorted = [...categories].sort((a, b) => a.ordinal - b.ordinal || a.code.localeCompare(b.code));
  const options: CategoryOption[] = sorted
    .filter((c) => !c.isRetired || c.code === current)
    .map((c) => ({
      value: c.code,
      label: c.isRetired ? `${c.code} · ${t('methodologies.constCategories.retired')}` : c.code,
    }));

  if (current !== '' && !options.some((o) => o.value === current)) {
    options.push({ value: current, label: `${current} · ${t('methodologies.constCategories.notInList')}` });
  }

  return options;
}

/**
 * Варіанти фільтра таблиці: «усі», «спільні», далі коди переліку і ті, що трапляються в константах.
 *
 * ⚠ Коди з констант додаються, навіть коли переліку не вдалося прочитати: фільтр таблиці не має
 * залежати від другого запиту.
 */
export function categoryFilterOptions(
  categories: readonly MethodologyCategoryDto[],
  constants: readonly MethodologyConstantDto[],
): CategoryOption[] {
  const codes: string[] = [];
  for (const c of [...categories].sort((a, b) => a.ordinal - b.ordinal || a.code.localeCompare(b.code))) {
    if (!codes.includes(c.code)) codes.push(c.code);
  }
  for (const c of constants) {
    if (c.category !== null && c.category !== '' && !codes.includes(c.category)) codes.push(c.category);
  }

  return [
    { value: SharedFilterValue, label: t('methodologies.categoryShared') },
    ...codes.map((code) => ({ value: code, label: code })),
  ];
}

/** Чи можна прибрати категорію: лише незайняту (сервер інакше відповість `409 categoryInUse`). */
export function mayDeleteCategory(category: MethodologyCategoryDto): boolean {
  return category.constantCount === 0;
}

/** Чи можна вивести з ужитку: лише незайняту (та сама відмова `409 categoryInUse`). */
export function mayRetireCategory(category: MethodologyCategoryDto): boolean {
  return !category.isRetired && category.constantCount === 0;
}
