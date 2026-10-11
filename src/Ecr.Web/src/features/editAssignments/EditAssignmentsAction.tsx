import { Suspense, lazy, useState, type JSX } from 'react';
import { Menu } from '@mantine/core';
import type { DocumentSummary } from '@/api/types';
import { can, useSession, type MeDto } from '@/shared/session/useSession';
import { t } from '@/shared/i18n';
import { useDetailPanel } from '@/shared/ui/DetailDrawer';
import { hasProjectGrant } from '@/features/documents/BusinessKeyChangeAction';

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
 * Чи показувати вхід: дзеркало `CanManageEditAssignments` (дизайн §2.4) настільки, наскільки його видно клієнту.
 *
 * - документ видимий (`Document.View`) і НЕ симуляція;
 * - грант `Manage` на проєкт - керує будь-хто з ним;
 * - грант `Write` - лише автор документа. ⚠ `DocumentSummary` віддає ім'я автора (`ownerDisplayName`), а не його
 *   id, тож клієнт не може порівняти автора з собою. Слабший за сервер гейт тут був би «Write - показуємо всім»;
 *   замість цього `Write` без `ownerDisplayName` (звужена роль: сервер не розкриває автора, D-214) не керує - так
 *   само, як на сервері. Точний гейт «автор» потребує `ownerUserId` у `DocumentSummary` (адитивно, серверна лінія).
 *   Остаточне рішення завжди за сервером: не автор отримає `403`, а не кнопки керування.
 */
export function canOpenEditAssignments(me: MeDto | undefined, document: DocumentSummary | undefined): boolean {
  if (document === undefined || me === undefined) return false;
  if (me.isSimulation || !can(me, 'Document.View')) return false;
  if (hasProjectGrant(me, document.projectId, 'Manage')) return true;

  return hasProjectGrant(me, document.projectId, 'Write') && document.ownerDisplayName != null;
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
