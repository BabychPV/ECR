import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/** Звіт переносу документа на нову версію шаблону (ФВ-7.5) — тип зі згенерованої схеми. */
export type VersionMigrationReport = components['schemas']['DocumentVersionMigrationDto'];

/** Поточна версія і версії, на які можна перенести документ. */
export type VersionMigrationTargets = components['schemas']['DocumentVersionMigrationTargetsDto'];

/** Режим переносу: `Safe` — без втрати даних, `Presentation` — лише зміни вигляду. */
export type VersionMigrationMode = components['schemas']['VersionMigrationMode'];

/**
 * Право, під яким сервер переносить документ на нову версію шаблону —
 * дзеркало `MigrateDocumentVersionHandler.Permission`.
 */
export const MigrateDocumentVersionPermission = 'Template.Edit';

/**
 * Версії, на які можна перенести документ: опубліковані версії того самого
 * шаблону, крім поточної.
 */
export function getVersionMigrationTargets(documentId: number): Promise<VersionMigrationTargets> {
  return apiFetch<VersionMigrationTargets>(`/api/v1/documents/${String(documentId)}/migrate-version`);
}

/** Параметри переносу. */
export interface MigrateDocumentVersionParams {
  readonly documentId: number;
  readonly targetVersionId: number;
  readonly mode: VersionMigrationMode;
  /** `true` — лише звіт, сервер нічого не змінює. */
  readonly dryRun: boolean;
}

/**
 * Переносить документ на нову версію шаблону (ФВ-7.5) або, з `dryRun`,
 * лише рахує наслідки.
 *
 * ⚠ Версія шаблону живе на проєкті: сервер переносить усі документи проєкту
 * разом, звіт каже скільки (`documentCount`). Режим без стратегії для змін —
 * `422` `ECR-SCHM-0422`; подані чи затверджені аркуші — `409` `ECR-DOC-0409`.
 */
export function migrateDocumentVersion(params: MigrateDocumentVersionParams): Promise<VersionMigrationReport> {
  return apiFetch<VersionMigrationReport>(`/api/v1/documents/${String(params.documentId)}/migrate-version`, {
    method: 'POST',
    body: JSON.stringify({
      targetVersionId: params.targetVersionId,
      mode: params.mode,
      dryRun: params.dryRun,
    } satisfies components['schemas']['MigrateDocumentVersionRequest']),
  });
}
