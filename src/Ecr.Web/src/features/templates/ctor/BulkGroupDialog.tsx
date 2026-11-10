import { useState, type JSX } from 'react';
import { Alert, Button, Group, Modal, Select, Text } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiFetch, EcrApiError } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import type { PresentationRevisionResponse, TemplateColumnDto, TemplateHeaderGroupDto } from '@/api/types';
import { localized } from '@/shared/i18n/localized';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';
import { draftsOf, flatten } from '../headerGroupTree';

/** Значення опції «— none —»: Mantine `Select` не тримає порожній рядок як вибране значення. */
export const NoGroup = 'none';

/** Ключ відмови «вигляд версії змінили після читання» (`PatchPresentationHandler.StaleRevision`). */
export const StaleRevisionMessageKey = 'err.ECR-TMPL-0409.presentationRevisionConflict';

/** Тіло `PATCH …/presentation`: одна зміна `HeaderGroupId` на кожну вибрану колонку. */
export function bulkGroupChanges(
  columns: readonly TemplateColumnDto[],
  groupId: string,
): { entityType: 'ColumnDef'; entityId: number; field: 'HeaderGroupId'; value: string | null }[] {
  return columns.map((column) => ({
    entityType: 'ColumnDef',
    entityId: column.id,
    field: 'HeaderGroupId',
    value: groupId === NoGroup ? null : groupId,
  }));
}

function isStale(error: unknown): boolean {
  return error instanceof EcrApiError && error.problem.extensions2?.['messageKey'] === StaleRevisionMessageKey;
}

/**
 * «Edit selected…» вкладки Columns (план PERMITES §3, макет `?dialog=edit-selected`, KIT §6.5.1): призначити
 * вибраним колонкам групу заголовка або зняти її. Рішення людини 10.11 — **все або нічого**: ОДИН
 * `PATCH …/presentation` на всі колонки з `expectedPresentationRevision`; сервер або застосовує все, або нічого.
 *
 * ⚠ Відмови показуються В діалозі, діалог лишається відкритим: 409 (вигляд змінили з іншої вкладки) —
 * структуру перечитано, людина бачить свіжі групи й застосовує ще раз; `ECR-TMPL-4229` (група не підряд) —
 * текст сервера.
 */
export function BulkGroupDialog({
  templateVersionId,
  presentationRevision,
  columns,
  headerGroups,
  opened,
  onClose,
  onApplied,
}: {
  readonly templateVersionId: number;
  readonly presentationRevision: number;
  readonly columns: readonly TemplateColumnDto[];
  readonly headerGroups: readonly TemplateHeaderGroupDto[] | undefined;
  readonly opened: boolean;
  readonly onClose: () => void;
  /** Успіх: сторінка знімає вибір. */
  readonly onApplied: () => void;
}): JSX.Element {
  const queryClient = useQueryClient();
  const [groupId, setGroupId] = useState<string | null>(null);
  const [stale, setStale] = useState(false);

  const groupIds = new Set(columns.map((column) => column.headerGroupId ?? null));
  const mixed = groupIds.size > 1;
  const current = groupIds.size === 1 ? [...groupIds][0] : null;
  // Поле показує спільну групу вибраних; різні групи — порожньо, доки людина не вибере.
  const value = groupId ?? (mixed ? null : current == null ? NoGroup : String(current));
  // ⛔ Нічого не змінилося — кнопка вимкнена: патч «тієї самої групи» писав би аудит змін, яких не було.
  const unchanged = !mixed && value === (current == null ? NoGroup : String(current));

  const options = [
    { value: NoGroup, label: t('columns.bulkNoGroup') },
    ...flatten(draftsOf(headerGroups)).map(({ group, depth }) => ({
      value: String(headerGroups?.find((item) => item.code === group.code)?.id ?? ''),
      label: `${'— '.repeat(depth - 1)}${localized({ values: group.label }) || group.code}`,
    })),
  ];

  function close(): void {
    setGroupId(null);
    setStale(false);
    patch.reset();
    onClose();
  }

  const patch = useMutation({
    mutationFn: (target: string) =>
      apiFetch<PresentationRevisionResponse>(
        `/api/v1/template-versions/${String(templateVersionId)}/presentation?expectedPresentationRevision=${String(presentationRevision)}`,
        { method: 'PATCH', body: JSON.stringify(bulkGroupChanges(columns, target)) },
      ),
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.templates.version(templateVersionId) });
      showDone(t('columns.bulkDone', { count: columns.length, revision: result.presentationRevision }));
      onApplied();
      close();
    },
    onError: async (error) => {
      if (isStale(error)) {
        setStale(true);
        // Перечитати структуру: нова ревізія й чужі зміни груп мають бути видні до повтору.
        await queryClient.invalidateQueries({ queryKey: queryKeys.templates.version(templateVersionId) });
      } else {
        setStale(false);
      }
    },
  });

  // ФВ-14.26: спінер не з першого кадру — пороги `useDurationIndicator`.
  const applyLoading = usePendingLoading(patch.isPending);
  const names = columns.map((column) => localized(column.headerL10n) || column.code).join(', ');

  return (
    <Modal opened={opened} onClose={close} title={t('columns.bulkTitle', { count: columns.length })}>
      <Text size="sm">{t('columns.bulkAppliesTo', { names })}</Text>
      {mixed && (
        <Text size="xs" c="dimmed" mt="xs">
          {t('columns.bulkMixed')}
        </Text>
      )}

      {patch.isError &&
        (stale ? (
          <Alert color="statusError" mt="sm" role="alert" data-bulk-error="stale">
            {t('columns.bulkStale')}
          </Alert>
        ) : (
          <div data-bulk-error="server">
            <ErrorAlert error={patch.error} />
          </div>
        ))}

      <Select
        mt="sm"
        label={t('colgroups.field')}
        description={t('colgroups.fieldHint')}
        data={options}
        allowDeselect={false}
        value={value}
        onChange={(next) => setGroupId(next)}
      />

      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={close}>
          {t('common.cancel')}
        </Button>
        <Button
          disabled={value === null || unchanged || columns.length === 0}
          loading={applyLoading}
          onClick={() => {
            if (value !== null) patch.mutate(value);
          }}
        >
          {t('columns.bulkApply')}
        </Button>
      </Group>
    </Modal>
  );
}
