import type { components } from '@/api/schema';

/** Рядок переліку типів дозволів (`GET /api/v1/permit-types`). */
export type PermitTypeListItem = components['schemas']['PermitTypeListItemDto'];

/** Короткий опис таблиці типу в переліку. */
export type PermitTypeTableSummary = components['schemas']['PermitTypeTableSummaryDto'];

/** Повне визначення типу: чернетка, якщо вона є, інакше опубліковане. */
export type PermitType = components['schemas']['PermitTypeDto'];

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
