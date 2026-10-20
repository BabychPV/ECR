import type { components } from '@/api/schema';

/** Рядок переліку типів дозволів (`GET /api/v1/permit-types`). */
export type PermitTypeListItem = components['schemas']['PermitTypeListItemDto'];

/** Короткий опис таблиці типу в переліку. */
export type PermitTypeTableSummary = components['schemas']['PermitTypeTableSummaryDto'];

/** Повне визначення типу: чернетка, якщо вона є, інакше опубліковане. */
export type PermitType = components['schemas']['PermitTypeDto'];

/** Використання типу даними: скільки дозволів і скільки рядків у кожній опублікованій таблиці (`GET …/{code}/usage`). */
export type PermitTypeUsage = components['schemas']['PermitTypeUsageDto'];

/** Кількість рядків в опублікованій таблиці типу, у всіх його дозволах. */
export type PermitTypeTableUsage = components['schemas']['PermitTypeTableUsageDto'];

/** Таблиця типу з усіма стовпцями й перевірками. */
export type PermitTypeTable = components['schemas']['PermitTypeTableDto'];

/** Стовпець таблиці типу (і поле шапки). */
export type PermitTypeColumn = components['schemas']['PermitTypeColumnDto'];

/** Перевірка типу чи таблиці: вираз, рівень `Warn`/`Block`. */
export type PermitTypeRule = components['schemas']['PermitTypeRuleDto'];

/** Запит на заведення типу (чернеткою; за потреби — копія іншого типу). */
export type CreatePermitTypeRequest = components['schemas']['CreatePermitTypeRequest'];

/** Запит на заміну чернетки визначення цілком. */
export type ReplacePermitTypeRequest = components['schemas']['ReplacePermitTypeRequest'];

/** Запит на публікацію чернетки: причина ≥ 10 символів і токен чернетки. */
export type PublishPermitTypeRequest = components['schemas']['PublishPermitTypeRequest'];

/** Рядок переліку дозволів (`GET /api/v1/permits`). */
export type PermitListItem = components['schemas']['PermitListItemDto'];

/** Дозвіл: шапка, стан, таблиці з підсумками, аркуші, що його читають (`GET /api/v1/permits/{id}`). */
export type Permit = components['schemas']['PermitDto'];

/** Таблиця дозволу: код приховуваного реєстру `PRM_…`, кількість рядків, головний підсумок. */
export type PermitTable = components['schemas']['PermitTableDto'];

/** Аркуш, що читає дозвіл (з PM-09; до того порожньо). */
export type PermitBoundSheet = components['schemas']['PermitBoundSheetDto'];

/** Прапорці звітних форм шапки дозволу. */
export type PermitReportingFlags = components['schemas']['PermitReportingFlags'];

/** Запит на заведення дозволу чернеткою. */
export type CreatePermitRequest = components['schemas']['CreatePermitRequest'];

/** Запит на зміну шапки дозволу (з токеном `rowVersion`). */
export type UpdatePermitRequest = components['schemas']['UpdatePermitRequest'];

/** Запит на дострокове завершення чинного дозволу. */
export type EndPermitRequest = components['schemas']['EndPermitRequest'];

/** Необов'язкові параметри поновлення. */
export type RenewPermitRequest = components['schemas']['RenewPermitRequest'];

/** Стан дозволу на дату. */
export type PermitStatus = 'Draft' | 'Active' | 'Expired';

/** Фільтри переліку дозволів. */
export interface PermitListParams {
  /** Код типу. */
  type?: string;
  status?: PermitStatus;
  q?: string;
  /** Дата, на яку рахується стан (`YYYY-MM-DD`); сервер за замовчуванням бере сьогодні (UTC). */
  asOf?: string;
  /** Розмір сторінки 1..500; сервер за замовчуванням дає 500 (екран рахує підсумки з повного переліку). */
  limit?: number;
  /** Id останнього дозволу попередньої сторінки. */
  cursor?: string;
}

/** Фільтри переліку типів. */
export interface PermitTypeListParams {
  category?: string;
  status?: string;
  q?: string;
  /** Розмір сторінки 1..500; сервер за замовчуванням дає 50. */
  limit?: number;
  /** Код останнього типу попередньої сторінки. */
  cursor?: string;
}
