import { Suspense, lazy, useState, type JSX } from 'react';
import { Menu } from '@mantine/core';
import type { DocumentSummary } from '@/api/types';
import { can, useSession, type MeDto } from '@/shared/session/useSession';
import { t } from '@/shared/i18n';
import { useDetailPanel } from '@/shared/ui/DetailDrawer';

/**
 * Сама шторка (і майстер, матриця) — окремим чанком: `DocumentPage` під межею бюджету (`D-132`),
 * потрібне лише тому, хто відкрив «Edit access». Стиль — як `VersionMigrationAction`.
 */
const loadDrawer = () => import('./EditAssignmentsDrawer');
const EditAssignmentsDrawer = lazy(async () => ({ default: (await loadDrawer()).EditAssignmentsDrawer }));

/** `?panel=` шторки; константа дубльована свідомо, щоб не тягнути чанк у статичний граф сторінки. */
const PanelId = 'edit-assignments';

interface EditAssignmentsActionArgs {
  readonly documentId: number;
  readonly document: DocumentSummary | undefined;
  readonly sheets: readonly { readonly code: string; readonly name: string }[];
}

interface EditAssignmentsAction {
  /** Пункт меню «More»; `null`, якщо права запису на проєкт немає. */
  readonly menuItem: JSX.Element | null;

  /** Шторка; `null`, доки її жодного разу не відкрито. */
  readonly drawer: JSX.Element | null;
}

/**
 * Чи показувати вхід: рішення СЕРВЕРА (`DocumentSummary.canManageEditAccess` = автор ∨ `Manage` проєкту, та сама
 * функція, що в обробниках `edit-assignments`) плюс «не симуляція» і видимий документ. Клієнт власного гейту не
 * вигадує: попередня евристика за грантами пускала в шторку не-автора з `Write` (403).
 */
export function canOpenEditAssignments(me: MeDto | undefined, document: DocumentSummary | undefined): boolean {
  if (document === undefined || me === undefined) return false;
  if (me.isSimulation || !can(me, 'Document.View')) return false;

  return document.canManageEditAccess === true;
}

/**
 * «Доступ до редагування» (передача місяця третій стороні): пункт «More → Edit access» і лінива шторка.
 */
export function useEditAssignmentsAction({ documentId, document, sheets }: EditAssignmentsActionArgs): EditAssignmentsAction {
  const session = useSession();
  const [panel, setPanel] = useDetailPanel();
  const [everOpened, setEverOpened] = useState(false);

  const allowed = canOpenEditAssignments(session.data, document);
  const open = panel === PanelId;
  if (open && !everOpened) setEverOpened(true);

  const menuItem = allowed ? (
    <Menu.Item onClick={() => setPanel(PanelId)} onPointerEnter={() => void loadDrawer()} data-edit-access="">
      {t('editAssign.menu')}
    </Menu.Item>
  ) : null;

  const drawer =
    allowed && (open || everOpened) ? (
      <Suspense fallback={null}>
        <EditAssignmentsDrawer documentId={documentId} sheets={sheets} />
      </Suspense>
    ) : null;

  return { menuItem, drawer };
}
