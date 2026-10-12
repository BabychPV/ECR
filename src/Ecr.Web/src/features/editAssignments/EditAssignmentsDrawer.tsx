import { useState, type JSX } from 'react';
import { Button, Group, SegmentedControl, Stack, Text } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import { formatMonthName } from '@/shared/format';
import { t } from '@/shared/i18n';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { DetailDrawer } from '@/shared/ui/DetailDrawer';
import { useSession } from '@/shared/session/useSession';
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
  upsertEditAssignment,
  returnEditAssignment,
  validateEditAssignment,
} from './api';
import { AssignWizard, emptyAssignData, resolveUsers, type AssignWizardData } from './AssignWizard';
import { AssignmentsMatrix, type MatrixActions, type MatrixView } from './AssignmentsMatrix';
import { awaitingValidationCount, makeSlice, nextMonthSlice, sameSlice, sliceLabel } from './model';
import { ReturnReasonMaxLength, type EditAssignment, type EditAssignmentsList } from './types';

/** Значення `?panel=` шторки (одне місце — меню й шторка читають звідси). */
export const EditAssignmentsPanelId = 'edit-assignments';

/** Значення `?dialog=` майстра. */
const WizardDialogId = 'edit-assignment-wizard';

/** Кого зачепить «Повернути» за замовчуванням: активні виконавці в стані `Filled`. */
function returnableNames(a: EditAssignment): string {
  return a.recipients
    .filter((r) => r.isActive && r.status === 'Filled')
    .map((r) => r.displayName)
    .join(', ');
}

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
  const [validating, setValidating] = useState<EditAssignment | null>(null);
  const [returning, setReturning] = useState<EditAssignment | null>(null);
  const [removing, setRemoving] = useState<EditAssignment | null>(null);

  // Симуляція «очима користувача» - лише перегляд: жодних переходів (дизайн §2.4: ¬симуляція).
  const session = useSession();
  const simulating = session.data?.isSimulation === true;

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

  // ⚠ `onError` оголошено в `mutate()`, а глобальна сітка `queryClient` дивиться на опції МУТАЦІЇ: без
  // `meta.handled` друга відмова показалась б двома тостами поспіль.
  const handled = { handled: true } as const;

  const validate = useMutation({
    meta: handled,
    mutationFn: (a: EditAssignment) => validateEditAssignment(documentId, a.id, a.version),
    onSuccess: (_, a) => {
      setValidating(null);
      done(t('editAssign.done.validated', { slice: sliceLabel(a.slice) }));
    },
    onError: showApiError,
    onSettled: refresh,
  });

  const giveBack = useMutation({
    meta: handled,
    mutationFn: ({ a, reason }: { a: EditAssignment; reason: string }) =>
      returnEditAssignment(documentId, a.id, { reason, baseVersion: a.version, userIds: null }),
    onSuccess: (_, { a }) => {
      setReturning(null);
      done(t('editAssign.done.returned', { slice: sliceLabel(a.slice) }));
    },
    onError: showApiError,
    onSettled: refresh,
  });

  const remove = useMutation({
    meta: handled,
    mutationFn: (a: EditAssignment) => deleteEditAssignment(documentId, a.id),
    onSuccess: (_, a) => {
      setRemoving(null);
      done(t('editAssign.done.removed', { slice: sliceLabel(a.slice) }));
    },
    onError: showApiError,
    onSettled: refresh,
  });

  const repeat = useMutation({
    meta: handled,
    mutationFn: (a: EditAssignment) => {
      const next = nextMonthSlice(a.slice);
      if (next === null) throw new Error('no next slice');

      return copyEditAssignment(documentId, { fromSlice: a.slice, toSlices: [next] });
    },
    onSuccess: (_, a) => done(t('editAssign.done.repeated', { month: formatMonthName((nextMonthSlice(a.slice)?.monthNumber ?? 1)) })),
    onError: showApiError,
    onSettled: refresh,
  });

  /** Застосування майстра: набір (за потреби) → по одному POST (upsert) на місяць; зупиняється на першій відмові. */
  const apply = useMutation({
    meta: handled,
    mutationFn: async (d: AssignWizardData) => {
      const users = resolveUsers(d, sets.data ?? []);
      let appliedSetId = d.setId === null ? undefined : Number(d.setId);

      if (d.saveAsSet && d.setName.trim() !== '') {
        const created = await createEditorSet({ name: d.setName.trim(), memberUserIds: users.map((u) => u.userId) });
        appliedSetId = created.id;
      }

      for (const month of d.months) {
        const slice = makeSlice({ monthNumber: month, sheetCode: d.sheetCode });
        const existing = assignments.find((a) => sameSlice(a.slice, slice));

        await upsertEditAssignment(documentId, {
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

  const actions: MatrixActions = {
    onValidate: (a: EditAssignment) => setValidating(a),
    onReturn: (a: EditAssignment) => setReturning(a),
    onRemove: (a: EditAssignment) => setRemoving(a),
    onRepeat: (a: EditAssignment) => repeat.mutate(a),
    onEdit: (a: EditAssignment) =>
      openWizard({
        ...emptyAssignData,
        months: a.slice.monthNumber == null ? [] : [a.slice.monthNumber],
        sheetCode: a.slice.sheetCode ?? null,
        users: a.recipients.map((r) => ({ userId: r.userId, displayName: r.displayName })),
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
          data?.canManage === true && !simulating ? (
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
              <Text size="sm">{t('editAssign.owner', { name: d.ownerDisplayName })}</Text>
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
              <AssignmentsMatrix view={view} assignments={d.assignments} actions={d.canManage && !simulating ? actions : undefined} />
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

      <ConfirmModal
        opened={validating !== null}
        title={t('editAssign.validateTitle', { slice: validating === null ? '' : sliceLabel(validating.slice) })}
        text={t('editAssign.validateText')}
        consequences={[t('editAssign.validateConsequence')]}
        verb={t('editAssign.validate')}
        danger={false}
        isPending={validate.isPending}
        onConfirm={() => {
          if (validating !== null) validate.mutate(validating);
        }}
        onClose={() => setValidating(null)}
      />

      <ReasonModal
        opened={returning !== null}
        title={t('editAssign.returnTitle', { slice: returning === null ? '' : sliceLabel(returning.slice) })}
        label={t('editAssign.returnReason')}
        description={t('editAssign.returnHint', {
          names: returning === null ? '' : returnableNames(returning),
          max: ReturnReasonMaxLength,
        })}
        confirmLabel={t('editAssign.returnConfirm')}
        minLength={1}
        maxLength={ReturnReasonMaxLength}
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
