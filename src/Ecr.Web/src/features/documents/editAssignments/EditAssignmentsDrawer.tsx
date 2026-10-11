import { useState, type JSX } from 'react';
import { Button, Group, SegmentedControl, Stack, Text } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import { formatMonthName } from '@/shared/format';
import { t } from '@/shared/i18n';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { DetailDrawer } from '@/shared/ui/DetailDrawer';
import { showApiError, showDone } from '@/shared/ui/notify';
import { problemText } from '@/shared/ui/problemText';
import { ReasonModal } from '@/shared/ui/ReasonModal';
import { useUrlState } from '@/shared/ui/useUrlState';
import {
  copyEditAssignment,
  createEditorSet,
  deleteEditAssignment,
  getEditAssignments,
  getEditorSets,
  putEditAssignment,
  returnEditAssignment,
  validateEditAssignment,
} from './api';
import { AssignWizard, emptyAssignData, resolveUsers, type AssignWizardData } from './AssignWizard';
import { AssignmentsMatrix, type MatrixView } from './AssignmentsMatrix';
import { awaitingValidationCount, nextMonthSlice, sameSlice, sliceLabel } from './model';
import type { EditAssignment, EditAssignmentsList } from './types';

/** Значення `?panel=` шторки (одне місце — меню й шторка читають звідси). */
export const EditAssignmentsPanelId = 'edit-assignments';

/** Значення `?dialog=` майстра. */
const WizardDialogId = 'edit-assignment-wizard';

export interface EditAssignmentsDrawerProps {
  readonly documentId: number;
  readonly sheets: readonly { readonly code: string; readonly name: string }[];
}

/**
 * Шторка «Доступ до редагування» (EA-5a): матриця «місяць × виконавець × статус», майстер
 * передачі, «Зберегти як набір», «Повторити для наступного місяця», Validate/Return.
 *
 * ⛔ Клієнт нічого не вирішує про права: що можна передати, кому і чи вистачає прав власника —
 * відповідає сервер (`422 delegationExceedsOwnRights` показується в кроці підсумку). Тут лише
 * показ станів і виклики переходів.
 */
export function EditAssignmentsDrawer({ documentId, sheets }: EditAssignmentsDrawerProps): JSX.Element {
  const queryClient = useQueryClient();
  const [panel] = useUrlState('panel');
  const [dialog, setDialog] = useUrlState('dialog');
  const opened = panel === EditAssignmentsPanelId;

  const [view, setView] = useState<MatrixView>('months');
  const [seed, setSeed] = useState<AssignWizardData>(emptyAssignData);
  const [announce, setAnnounce] = useState('');
  const [returning, setReturning] = useState<EditAssignment | null>(null);
  const [removing, setRemoving] = useState<EditAssignment | null>(null);

  const query = useQuery({
    queryKey: queryKeys.editAssignments.forDocument(documentId),
    queryFn: () => getEditAssignments(documentId),
    enabled: opened,
  });
  const sets = useQuery({ queryKey: queryKeys.editAssignments.editorSets(), queryFn: getEditorSets, enabled: opened && dialog === WizardDialogId });

  const refresh = (): Promise<void> => queryClient.invalidateQueries({ queryKey: queryKeys.editAssignments.all() });
  const done = (message: string): void => {
    setAnnounce(message);
    showDone(message);
  };

  const data: EditAssignmentsList | undefined = query.data;
  const assignments = data?.assignments ?? [];
  const awaiting = awaitingValidationCount(assignments);

  const validate = useMutation({
    mutationFn: (a: EditAssignment) => validateEditAssignment(documentId, a.id, a.version),
    onSuccess: (_, a) => done(t('editAssign.done.validated', { slice: sliceLabel(a.slice) })),
    onError: showApiError,
    onSettled: refresh,
  });

  const giveBack = useMutation({
    mutationFn: ({ a, reason }: { a: EditAssignment; reason: string }) =>
      returnEditAssignment(documentId, a.id, { reason, baseVersion: a.version }),
    onSuccess: (_, { a }) => {
      setReturning(null);
      done(t('editAssign.done.returned', { slice: sliceLabel(a.slice) }));
    },
    onError: showApiError,
    onSettled: refresh,
  });

  const remove = useMutation({
    mutationFn: (a: EditAssignment) => deleteEditAssignment(documentId, a.id),
    onSuccess: (_, a) => {
      setRemoving(null);
      done(t('editAssign.done.removed', { slice: sliceLabel(a.slice) }));
    },
    onError: showApiError,
    onSettled: refresh,
  });

  const repeat = useMutation({
    mutationFn: (a: EditAssignment) => {
      const next = nextMonthSlice(a.slice);
      if (next === null) throw new Error('no next slice');

      return copyEditAssignment(documentId, { fromSlice: a.slice, toSlices: [next] });
    },
    onSuccess: (_, a) => done(t('editAssign.done.repeated', { month: formatMonthName((nextMonthSlice(a.slice)?.monthNumber ?? 1)) })),
    onError: showApiError,
    onSettled: refresh,
  });

  /** Застосування майстра: набір (за потреби) → по одному PUT на місяць; зупиняється на першій відмові. */
  const apply = useMutation({
    mutationFn: async (d: AssignWizardData) => {
      const users = resolveUsers(d, sets.data ?? []);
      let appliedSetId = d.setId === null ? undefined : Number(d.setId);

      if (d.saveAsSet && d.setName.trim() !== '') {
        const created = await createEditorSet({ name: d.setName.trim(), userIds: users.map((u) => u.userId) });
        appliedSetId = created.id;
      }

      for (const month of d.months) {
        const slice = { monthNumber: month, ...(d.sheetCode === null ? {} : { sheetCode: d.sheetCode }) };
        const existing = assignments.find((a) => sameSlice(a.slice, slice));

        await putEditAssignment(documentId, {
          slice,
          recipientUserIds: users.map((u) => u.userId),
          isExclusive: !d.alsoOthers,
          ...(d.note.trim() === '' ? {} : { note: d.note.trim() }),
          ...(appliedSetId === undefined ? {} : { appliedSetId }),
          ...(existing === undefined ? {} : { baseVersion: existing.version }),
        });
      }
    },
    onSettled: refresh,
  });

  const openWizard = (next: AssignWizardData): void => {
    setSeed(next);
    setDialog(WizardDialogId);
  };

  const actions = {
    onValidate: (a: EditAssignment) => validate.mutate(a),
    onReturn: (a: EditAssignment) => setReturning(a),
    onRemove: (a: EditAssignment) => setRemoving(a),
    onRepeat: (a: EditAssignment) => repeat.mutate(a),
    onEdit: (a: EditAssignment) =>
      openWizard({
        ...emptyAssignData,
        months: a.slice.monthNumber == null ? [] : [a.slice.monthNumber],
        sheetCode: a.slice.sheetCode ?? null,
        users: a.recipients.map((r) => ({ userId: r.userId, displayName: r.displayName, visibility: r.visibility })),
        note: a.note ?? '',
        alsoOthers: !a.isExclusive,
      }),
  };

  return (
    <>
      <DetailDrawer
        panelId={EditAssignmentsPanelId}
        title={t('editAssign.title')}
        closeLabel={t('editAssign.close')}
        size="xl"
        footer={
          data?.canManage === true ? (
            <Button onClick={() => openWizard(emptyAssignData)} data-autofocus>
              {t('editAssign.assign')}
            </Button>
          ) : undefined
        }
      >
        <AsyncBoundary<EditAssignmentsList>
          isPending={query.isPending}
          error={query.error}
          data={data}
          onRetry={() => void query.refetch()}
          isEmpty={(d) => d.assignments.length === 0}
          emptyTitle={t('editAssign.empty')}
          emptyHint={t('editAssign.emptyHint')}
        >
          {(d) => (
            <Stack gap="md">
              <Text size="sm" c="dimmed">
                {t('editAssign.hint')}
              </Text>
              <Text size="sm">{t('editAssign.owner', { name: d.owner.displayName })}</Text>
              {awaiting > 0 ? (
                <Text size="sm" fw={500} data-testid="edit-assignments-awaiting">
                  {t('editAssign.awaiting', { count: awaiting })}
                </Text>
              ) : null}
              <Group justify="space-between">
                <SegmentedControl
                  value={view}
                  onChange={(value) => setView(value as MatrixView)}
                  aria-label={t('editAssign.view')}
                  data={[
                    { value: 'months', label: t('editAssign.view.months') },
                    { value: 'people', label: t('editAssign.view.people') },
                  ]}
                />
              </Group>
              <AssignmentsMatrix view={view} assignments={d.assignments} actions={actions} />
            </Stack>
          )}
        </AsyncBoundary>
        {/* Результат дії для читалки — ввічливий live-регіон, а не лише тост. */}
        <div role="status" aria-live="polite" data-testid="edit-assignments-live" style={{ minHeight: 0 }}>
          {announce}
        </div>
      </DetailDrawer>

      {opened && dialog === WizardDialogId ? (
        <AssignWizard
          documentId={documentId}
          sheets={sheets}
          initialData={seed}
          takenMonths={assignments.flatMap((a) => (a.slice.monthNumber == null ? [] : [a.slice.monthNumber]))}
          isApplying={apply.isPending}
          onClose={() => setDialog(null)}
          onApply={(d, api) =>
            apply.mutate(d, {
              onSuccess: () => {
                done(t('editAssign.done.assigned', { count: d.months.length }));
                api.close();
              },
              onError: (error) => {
                const shown = problemText(error);
                api.fail(shown.detail ?? shown.title);
              },
            })
          }
        />
      ) : null}

      <ReasonModal
        opened={returning !== null}
        title={t('editAssign.returnTitle', { slice: returning === null ? '' : sliceLabel(returning.slice) })}
        label={t('editAssign.returnReason')}
        confirmLabel={t('editAssign.return')}
        isPending={giveBack.isPending}
        onConfirm={(reason) => {
          if (returning !== null) giveBack.mutate({ a: returning, reason });
        }}
        onClose={() => setReturning(null)}
      />

      <ConfirmModal
        opened={removing !== null}
        title={t('editAssign.removeTitle', { slice: removing === null ? '' : sliceLabel(removing.slice) })}
        text={t('editAssign.removeText')}
        verb={t('editAssign.remove')}
        isPending={remove.isPending}
        onConfirm={() => {
          if (removing !== null) remove.mutate(removing);
        }}
        onClose={() => setRemoving(null)}
      />
    </>
  );
}
