import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/** Звіт переносу документа на нову версію шаблону (ФВ-7.5) — тип зі згенерованої схеми. */
export type VersionMigrationReport = components['schemas']['DocumentVersionMigrationDto'];

/** Прийнятий у чергу фоновий перенос: `jobId` для `GET /jobs/{jobId}` (D-2 RC15B). */
export type MigrationAccepted = components['schemas']['MigrationAcceptedResponse'];

/** Поточна версія і версії, на які можна перенести документ. */
type VersionMigrationTargets = components['schemas']['DocumentVersionMigrationTargetsDto'];

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
interface MigrateDocumentVersionParams {
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
      // Синхронний шлях (сухий прогін завжди синхронний; apply у фоні йде через `startMigrateDocumentVersion`).
      async: false,
    } satisfies components['schemas']['MigrateDocumentVersionRequest']),
  });
}

/**
 * Ставить перенос на нову версію шаблону у ФОН (D-2 RC15B): `202` з `jobId`, а не звіт.
 *
 * ⛔ Перенос великого проєкту (мільйони значень) триває десятки хвилин — довше за таймаут проксі й браузера, тож
 * синхронний запит обривався б, а перенос лишався неочевидним. Стан, прогрес і підсумок — `GET /jobs/{jobId}`
 * (`useJobStatus`). Права й ціль сервер перевіряє одразу (`404`/`403`/`409`/`422`); відмову за даними
 * (`ECR-SCHM-0422`) задача повертає станом `Failed`, документи лишаються на старій версії.
 */
export function startMigrateDocumentVersion(
  params: Omit<MigrateDocumentVersionParams, 'dryRun'>,
): Promise<MigrationAccepted> {
  return apiFetch<MigrationAccepted>(`/api/v1/documents/${String(params.documentId)}/migrate-version`, {
    method: 'POST',
    body: JSON.stringify({
      targetVersionId: params.targetVersionId,
      mode: params.mode,
      dryRun: false,
      async: true,
    } satisfies components['schemas']['MigrateDocumentVersionRequest']),
  });
}
