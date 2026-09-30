import { Suspense, lazy, useState, type JSX } from 'react';
import { Menu } from '@mantine/core';
import type { DocumentSummary } from '@/api/types';
import { can, useSession } from '@/shared/session/useSession';
import { t } from '@/shared/i18n';
import { MigrateDocumentVersionPermission } from './versionMigrationApi';

/**
 * Сам діалог — окремим чанком.
 *
 * ⛔ Не «про запас»: із діалогом у статичному графі маршрут `DocumentPage`
 * виходив на 255.4 КБ gzip при межі 250 (`D-132`, `check-bundle-budget.mjs`) —
 * `SegmentedControl`, `Table` і сам діалог потрібні лише тому, хто відкрив
 * перенос. Стиль — `loadX`/`lazy(async () => ...)`, як у `DocumentPage.tsx`.
 */
const loadVersionMigrationDialog = () => import('./VersionMigrationDialog');
const VersionMigrationDialog = lazy(async () => ({
  default: (await loadVersionMigrationDialog()).VersionMigrationDialog,
}));

interface VersionMigrationActionArgs {
  readonly documentId: number;

  /** `undefined` — документ ще не приїхав: пункту немає. */
  readonly document: DocumentSummary | undefined;
}

interface VersionMigrationAction {
  /** Пункт меню «More» сторінки документа; `null`, якщо права немає. */
  readonly menuItem: JSX.Element | null;

  /** Діалог — окремо від пункту: меню розмонтовує вміст, щойно закривається. */
  readonly dialog: JSX.Element | null;
}

/**
 * «Перенести на нову версію шаблону» (ФВ-7.5): пункт меню за правом
 * `Template.Edit` і лінивий діалог сухого прогону й переносу
 * (`VersionMigrationDialog.tsx`).
 *
 * ⚠ Діалог монтується лише відкритим і розмонтовується при закритті — кожне
 * відкриття починає з чистого вибору й без звіту.
 */
export function useVersionMigrationAction({ documentId, document }: VersionMigrationActionArgs): VersionMigrationAction {
  const session = useSession();
  const [opened, setOpened] = useState(false);

  const allowed = can(session.data, MigrateDocumentVersionPermission) && document !== undefined;

  const menuItem = allowed ? (
    <Menu.Item onClick={() => setOpened(true)} data-migrate-version="">
      {t('documents.migrateVersion')}
    </Menu.Item>
  ) : null;

  const dialog =
    allowed && opened ? (
      <Suspense fallback={null}>
        <VersionMigrationDialog documentId={documentId} onClose={() => setOpened(false)} />
      </Suspense>
    ) : null;

  return { menuItem, dialog };
}
