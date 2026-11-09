import { useEffect, useState, type JSX, type ReactNode } from 'react';
import { Alert, Button, Group, Modal, Stack, Text, TextInput } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import type { CloneProjectRequest, PeriodCalendarDto, ProjectIdResponse } from '@/api/types';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { hasProjectGrant } from '@/features/documents/projectGrant';
import { useDocumentListSummary } from '@/features/documents/api';
import { projectsQuery } from '@/features/projects/allProjects';
import { MountedLazyConfirmModal } from '@/features/projects/lazyPeriodsDialogs';
import { projectTabPath } from '@/features/projects/projectPaths';
import { useDeleteProject } from '@/features/projects/deleteProject';
import { archiveBlock, calendarYears, deleteAllowed, monthList, type ArchiveBlock } from '@/features/projects/projectYear';
import { t } from '@/shared/i18n';
import { can, useSession } from '@/shared/session/useSession';
import { showApiError, showDone } from '@/shared/ui/notify';
import { statusKey } from '@/shared/ui/StatusBadge';
import './projects.css';

/** Документів у проєкті за його роки (зведення сервера за рік — та сама межа видимості, що й перелік). */
export function documentTotal(summary: {
  readonly draft: number;
  readonly partlySubmitted: number;
  readonly submitted: number;
  readonly approved: number;
  readonly rejected: number;
}): number {
  return summary.draft + summary.partlySubmitted + summary.submitted + summary.approved + summary.rejected;
}

/** Причина недоступності архівації — видимим текстом (рядок c3). */
export function archiveReason(block: ArchiveBlock): string {
  switch (block.kind) {
    case 'archived':
      return t('projects.lifecycle.archive.already');
    case 'notActive':
      return t('projects.lifecycle.archive.notActive', { status: t(statusKey('project', block.status)) });
    case 'openMonths':
      return t('projects.lifecycle.archive.openMonths', { months: monthList(block.months) });
    default:
      return t('projects.lifecycle.unknown');
  }
}

/** Причина недоступності активації (рядок c4: причина видна, а не лише сіра кнопка). */
export function activateReason(status: string): string | null {
  if (status === 'Draft') return null;

  return status === 'Active' ? t('projects.lifecycle.activate.already') : t('projects.lifecycle.activate.archived');
}

/** Рядок дії: кнопка з повною назвою + опис; недоступна — причина видимим текстом під описом (c1–c4). */
function ActionRow({
  id,
  button,
  description,
  reason,
}: {
  readonly id: string;
  readonly button: (describedBy: string) => ReactNode;
  readonly description: string;
  readonly reason: string | null;
}): JSX.Element {
  const describedBy = `lifecycle-${id}-desc`;

  return (
    <div className="ecr-lifecycle-row" data-lifecycle-action={id}>
      {button(describedBy)}
      <Stack gap="xs" id={describedBy}>
        <Text size="sm">{description}</Text>
        {reason === null ? (
          <Text size="xs" c="dimmed">
            {t('projects.lifecycle.available')}
          </Text>
        ) : (
          <Text size="sm" fw={500} c="statusWarning" data-lifecycle-reason={id}>
            {reason}
          </Text>
        )}
      </Stack>
    </div>
  );
}

/**
 * ✎ Projects (макет 08.11, рядки f6, c1–c4): життєвий цикл проєкту — Activate, Clone, Archive і Delete draft (видалення —
 * `DELETE /api/v1/projects/{id}` lane A, лише чернетка без документів, з набором коду проєкту). Кожна дія — повна назва й рядок опису; недоступна — причина видимим текстом під нею.
 *
 * ⛔ `F-19`: право каже, ЩО можна, грант Manage — НАД ЧИМ; без обох кнопка обіцяла б 403.
 * ⛔ Архівація: лише активний і лише коли всі місяці заблоковані (`D-123`); невідомий календар — заборона.
 */
export function ProjectLifecycleTab({ projectId }: { readonly projectId: number }): JSX.Element | null {
  const session = useSession();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const projects = useQuery({ ...projectsQuery });
  const project = projects.data?.items.find((item) => item.id === projectId);
  const calendar = useQuery({
    queryKey: ['periods', projectId],
    queryFn: () => apiFetch<PeriodCalendarDto>(`/api/v1/projects/${String(projectId)}/periods`),
  });
  const year = calendarYears(calendar.data).at(-1) ?? null;
  const summary = useDocumentListSummary(null, undefined, projectId, year);
  const documents = summary.data === undefined ? null : documentTotal(summary.data);

  const [confirmingActivate, setConfirmingActivate] = useState(false);
  const [archiving, setArchiving] = useState(false);
  const [cloning, setCloning] = useState(false);
  const [cloneCode, setCloneCode] = useState('');
  const [deleting, setDeleting] = useState(false);
  const [deleteCode, setDeleteCode] = useState('');

  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: projectsQuery.queryKey });
    await queryClient.invalidateQueries({ queryKey: ['periods', projectId] });
  };

  const activate = useMutation({
    mutationFn: () => apiFetch(`/api/v1/projects/${String(projectId)}/activate`, { method: 'POST' }),
    onSuccess: async () => {
      await refresh();
      showDone(t('periods.activated'));
    },
    onError: showApiError,
  });

  const archive = useMutation({
    mutationFn: () => apiFetch(`/api/v1/projects/${String(projectId)}/archive`, { method: 'POST' }),
    onSuccess: async () => {
      await refresh();
      setArchiving(false);
      showDone(t('periods.archived'));
    },
    onError: showApiError,
  });

  // ⚠ Клон копіює довідники, налаштування і склад аркушів; ДАНІ — ні (`ФВ-1.3`). Новий — чернетка, далі Settings.
  const clone = useMutation({
    mutationFn: () =>
      apiFetch<ProjectIdResponse>(`/api/v1/projects/${String(projectId)}/clone`, {
        method: 'POST',
        body: JSON.stringify({ code: cloneCode.trim() } satisfies CloneProjectRequest),
      }),
    onSuccess: async (result) => {
      await refresh();
      setCloning(false);
      setCloneCode('');
      showDone(t('periods.cloned'));
      void navigate(projectTabPath(result.projectId, 'settings'));
    },
    onError: showApiError,
  });

  // ФВ-14.26: спінер — лише коли дія триває довше за поріг (`usePendingLoading`), а не з першого кадру.
  const activating = usePendingLoading(activate.isPending);
  const cloningBusy = usePendingLoading(clone.isPending);
  const archivingBusy = usePendingLoading(archive.isPending);

  // c1 (lane A: `DELETE /api/v1/projects/{id}`): після видалення — перелік проєктів і тост.
  const deletedCode = project?.code ?? '';
  const remove = useDeleteProject(() => {
    setDeleting(false);
    showDone(t('projects.deleted', { code: deletedCode }));
    void navigate('/projects');
  });
  const deletingBusy = usePendingLoading(remove.isPending);
  useEffect(() => {
    if (remove.error !== null) showApiError(remove.error);
  }, [remove.error]);

  if (project === undefined) return null;

  const manages = can(session.data, 'Project.Manage') && hasProjectGrant(session.data ?? undefined, projectId, 'Manage');
  const noRight = manages ? null : t('projects.lifecycle.noRight');
  const activateWhy = noRight ?? activateReason(project.status);
  const block = archiveBlock(project, calendar.data);
  const archiveWhy = noRight ?? (block === null ? null : archiveReason(block));
  // c1/c2: лише чернетка без документів; кількість невідома — заборона з причиною «перевіряємо», а не дозвіл.
  const deleteWhy =
    noRight ??
    (deleteAllowed(project, documents)
      ? null
      : documents === null
        ? t('projects.lifecycle.unknown')
        : t('projects.lifecycle.delete.notAllowed', {
            status: t(statusKey('project', project.status)),
            count: documents,
          }));

  return (
    <>
      <div className="ecr-lifecycle" data-project-lifecycle="">
        <ActionRow
          id="activate"
          description={t('projects.lifecycle.activate.desc')}
          reason={activateWhy}
          button={(describedBy) => (
            <Button
              disabled={activateWhy !== null}
              loading={activating}
              onClick={() => setConfirmingActivate(true)}
              aria-describedby={describedBy}
            >
              {t('projects.lifecycle.activate')}
            </Button>
          )}
        />
        <ActionRow
          id="clone"
          description={t('projects.lifecycle.clone.desc')}
          reason={noRight}
          button={(describedBy) => (
            <Button variant="default" disabled={noRight !== null} onClick={() => setCloning(true)} aria-describedby={describedBy}>
              {t('projects.lifecycle.clone')}
            </Button>
          )}
        />
        <ActionRow
          id="archive"
          description={t('projects.lifecycle.archive.desc')}
          reason={archiveWhy}
          button={(describedBy) => (
            <Button
              variant="outline"
              color="statusError"
              disabled={archiveWhy !== null}
              onClick={() => setArchiving(true)}
              aria-describedby={describedBy}
            >
              {t('projects.lifecycle.archive')}
            </Button>
          )}
        />
        <ActionRow
          id="delete"
          description={t('projects.lifecycle.delete.desc')}
          reason={deleteWhy}
          button={(describedBy) => (
            <Button
              color="statusError"
              disabled={deleteWhy !== null}
              onClick={() => {
                setDeleteCode('');
                setDeleting(true);
              }}
              aria-describedby={describedBy}
            >
              {t('projects.lifecycle.delete')}
            </Button>
          )}
        />
      </div>
      {documents !== null && (
        <Text size="xs" c="dimmed" mt="xs" data-project-documents={documents}>
          {t('projects.lifecycle.documents', { count: documents })}
        </Text>
      )}

      {/* c1: видалення чернетки — лише після набору коду проєкту; незворотне, сервер пише запис аудиту. */}
      <Modal opened={deleting} onClose={() => setDeleting(false)} title={t('projects.delete.title', { code: project.code })}>
        <Stack gap="xs">
          <Text size="sm" c="statusError">
            {t('projects.delete.consequence', { code: project.code })}
          </Text>
          <Text size="sm" c="dimmed">
            {t('projects.delete.noData')}
          </Text>
          <Text size="sm" c="dimmed">
            {t('projects.delete.audit')}
          </Text>
          <TextInput
            label={t('projects.delete.typeCode', { code: project.code })}
            value={deleteCode}
            onChange={(event) => setDeleteCode(event.currentTarget.value)}
            autoComplete="off"
            spellCheck={false}
            ff="monospace"
            data-autofocus
          />
        </Stack>
        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setDeleting(false)}>
            {t('common.cancel')}
          </Button>
          <Button
            color="statusError"
            disabled={deleteCode !== project.code}
            loading={deletingBusy}
            onClick={() => remove.mutate(projectId)}
          >
            {t('projects.delete.confirm')}
          </Button>
        </Group>
      </Modal>

      <MountedLazyConfirmModal
        opened={confirmingActivate}
        title={t('periods.activateTitle', { code: project.code })}
        text={t('periods.activateConfirm')}
        verb={t('periods.activate')}
        danger={false}
        isPending={activate.isPending}
        onConfirm={() => {
          activate.mutate();
          setConfirmingActivate(false);
        }}
        onClose={() => setConfirmingActivate(false)}
      />

      <Modal opened={cloning} onClose={() => setCloning(false)} title={t('periods.clone')}>
        <Text size="sm" mb="sm">
          {t('periods.cloneHint')}
        </Text>
        <TextInput
          label={t('periods.code')}
          value={cloneCode}
          onChange={(event) => setCloneCode(event.currentTarget.value)}
          data-autofocus
        />
        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setCloning(false)}>
            {t('common.cancel')}
          </Button>
          <Button disabled={cloneCode.trim().length === 0} loading={cloningBusy} onClick={() => clone.mutate()}>
            {t('periods.clone')}
          </Button>
        </Group>
      </Modal>

      <Modal opened={archiving} onClose={() => setArchiving(false)} title={t('periods.archive')}>
        <Text size="sm" mb="sm">
          {t('periods.archiveConfirm')}
        </Text>
        {block !== null && (
          <Alert color="statusWarning" variant="light" mb="sm">
            {archiveReason(block)}
          </Alert>
        )}
        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setArchiving(false)}>
            {t('common.cancel')}
          </Button>
          <Button color="statusError" loading={archivingBusy} disabled={block !== null} onClick={() => archive.mutate()}>
            {t('periods.archive')}
          </Button>
        </Group>
      </Modal>
    </>
  );
}
