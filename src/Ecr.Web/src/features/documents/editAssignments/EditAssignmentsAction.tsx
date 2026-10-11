import { Suspense, lazy, useState, type JSX } from 'react';
import { Menu } from '@mantine/core';
import type { DocumentSummary } from '@/api/types';
import { can, useSession } from '@/shared/session/useSession';
import { t } from '@/shared/i18n';
import { useDetailPanel } from '@/shared/ui/DetailDrawer';
import { hasProjectGrant } from '../BusinessKeyChangeAction';

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
 * «Доступ до редагування» (передача місяця третій стороні): пункт «More → Edit access» і лінива шторка.
 *
 * ⚠ Пункт показується за грантом запису на проєкт — дзеркало першої половини `CanManageEditAssignments`
 * (дизайн §2.4). Остаточне рішення (автор документа чи `Manage`) за сервером: без права
 * шторка покаже 403, а не кнопки керування.
 */
export function useEditAssignmentsAction({ documentId, document, sheets }: EditAssignmentsActionArgs): EditAssignmentsAction {
  const session = useSession();
  const [panel, setPanel] = useDetailPanel();
  const [everOpened, setEverOpened] = useState(false);

  // `Document.View` — бо сервер керує лише над ВИДИМИМ документом (дизайн §2.4).
  const allowed = document !== undefined && can(session.data, 'Document.View') && hasProjectGrant(session.data, document.projectId, 'Write');
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
