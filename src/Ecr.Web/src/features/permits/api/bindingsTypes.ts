import type { components } from '@/api/schema';

/** Прив'язки версії шаблону разом зі станом версії (`GET …/permit-bindings`). */
export type PermitBindings = components['schemas']['PermitBindingsDto'];

/** Прив'язка таблиці аркуша до таблиці типу дозволу. */
export type PermitBinding = components['schemas']['PermitBindingDto'];

/** Зіставлена колонка прив'язки у відповіді. */
export type PermitBindingColumn = components['schemas']['PermitBindingColumnDto'];

/** Запит на створення чи заміну прив'язки (`PUT …/permit-bindings/{code}`). */
export type SavePermitBindingRequest = components['schemas']['SavePermitBindingRequest'];

/** Зіставлена колонка в запиті. */
export type PermitBindingColumnRequest = components['schemas']['PermitBindingColumnRequest'];

/** Перегляд аркуша очима користувача (`GET …/permit-bindings/{code}/preview`). */
export type PermitBindingPreview = components['schemas']['PermitBindingPreviewDto'];

/** Обраний для перегляду дозвіл. */
export type PermitPreviewPermit = components['schemas']['PermitPreviewPermitDto'];

/** Колонка аркуша в перегляді з роллю. */
export type PermitPreviewColumn = components['schemas']['PermitPreviewColumnDto'];

/** Рядок аркуша в перегляді. */
export type PermitPreviewRow = components['schemas']['PermitPreviewRowDto'];

/** Попередження перегляду. */
export type PermitPreviewWarning = components['schemas']['PermitPreviewWarningDto'];

/**
 * Джерело даних перегляду. Без `documentId` потрібні `periodEnd` (інакше сьогодні) і `facilityName` чи
 * `permitEntryId` залежно від правила; з `documentId` — ще й `periodKey` (`РРРРММ`).
 */
export interface PermitPreviewParams {
  documentId?: number;
  periodKey?: number;
  facilityName?: string;
  /** `yyyy-MM-dd`. */
  periodEnd?: string;
  permitEntryId?: number;
}
