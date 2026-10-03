import { useEffect, useState, type JSX } from 'react';
import { useReturnFocusOnUnmount } from '@/shared/a11y/focus';
import { Alert, Button, Group, Modal, SegmentedControl, Select, Stack, Table, Text } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';
import { whenEditsSaved } from '@/features/grid/settleEdits';
import {
  getVersionMigrationTargets,
  migrateDocumentVersion,
  type VersionMigrationMode,
  type VersionMigrationReport,
} from './versionMigrationApi';

/** Скільки відмінностей показувати в діалозі; решта — лічильником. */
const ShownItems = 50;

/**
 * Підпис причини відмови. Ключі — ЛІТЕРАЛАМИ, а не складанням рядка: сторож
 * каталогу шукає в клієнті саме літерали, і складений ключ пройшов би повз.
 */
export function refusalText(reason: string): string {
  switch (reason) {
    case 'structural':
      return t('documents.migrateRefusalStructural');
    case 'dataLoss':
      return t('documents.migrateRefusalDataLoss');
    case 'guardedWithData':
      return t('documents.migrateRefusalGuarded');
    case 'sheetsLocked':
      return t('documents.migrateRefusalSheetsLocked');
    case 'projectArchived':
      return t('documents.migrateRefusalArchived');
    case 'grantsNotMapped':
      return t('documents.migrateRefusalGrantsNotMapped');
    default:
      return reason;
  }
}

/** Підпис виду відмінності; ключі — літералами (див. {@link refusalText}). */
export function itemKindText(kind: string): string {
  switch (kind) {
    case 'Added':
      return t('documents.migrateKindAdded');
    case 'Removed':
      return t('documents.migrateKindRemoved');
    case 'Lost':
      return t('documents.migrateKindLost');
    case 'Modified':
      return t('documents.migrateKindModified');
    case 'Presentation':
      return t('documents.migrateKindPresentation');
    default:
      return kind;
  }
}

export interface VersionMigrationDialogProps {
  readonly documentId: number;

  /** Закрити діалог (скасування або успішний перенос). */
  readonly onClose: () => void;
}

/**
 * «Перенести на нову версію шаблону» (ФВ-7.5): явна операція з попереднім
 * переглядом наслідків.
 *
 * ⛔ Кнопка переносу доступна ЛИШЕ після сухого прогону для ТІЄЇ САМОЇ пари
 * «версія × режим», і лише якщо звіт каже «можна». Будь-яка зміна вибору
 * скидає звіт: підтверджувати людина мусить те, що бачила, а не попередній
 * вибір. Сервер однаково перераховує план під блоком проєкту — клієнтська
 * умова лише не дає натиснути навмання.
 *
 * ⚠ Версія шаблону живе на проєкті, тож переносяться всі документи проєкту —
 * звіт каже скільки, і діалог показує це першим рядком.
 *
 * ⚠ Діалог монтується заново на кожне відкриття (див. `VersionMigrationAction.tsx`),
 * тож вибір і звіт щоразу починаються з нуля без окремого скидання.
 */
export function VersionMigrationDialog({ documentId, onClose }: VersionMigrationDialogProps): JSX.Element {
  useReturnFocusOnUnmount();
  const queryClient = useQueryClient();

  const [targetId, setTargetId] = useState<string | null>(null);
  const [mode, setMode] = useState<VersionMigrationMode>('Safe');
  // AN-39/L8-09: звіт несе ключ вибору, для якого його отримано; показується лише за збігу.
  const [reportState, setReportState] = useState<{ key: string; result: VersionMigrationReport } | null>(null);
  const selectionKey = `${targetId ?? ''}|${mode}`;
  const report = reportState !== null && reportState.key === selectionKey ? reportState.result : null;

  const targets = useQuery({
    queryKey: ['document', documentId, 'migrate-version-targets'],
    queryFn: () => getVersionMigrationTargets(documentId),
  });

  // AN-39/L8-14: відмову показує ErrorAlert у діалозі (`dryRun.error`/`apply.error`) -
  // без `handled` глобальна сітка додавала другий тост.
  const dryRun = useMutation({
    meta: { handled: true },
    mutationFn: (key: string) =>
      migrateDocumentVersion({ documentId, targetVersionId: Number(targetId), mode, dryRun: true }).then(
        (result) => ({ key, result }),
      ),
    onSuccess: (done) => setReportState(done),
  });

  const apply = useMutation({
    meta: { handled: true },
    mutationFn: () =>
      migrateDocumentVersion({ documentId, targetVersionId: Number(targetId), mode, dryRun: false }),
    onSuccess: async (result) => {
      // ⚠ Змінилась структура ВСІХ документів проєкту: аркуші, таблиці,
      // колонки. Вузька інвалідація тут лише ризикувала б лишити засталу сітку.
      await queryClient.invalidateQueries();
      showDone(t('documents.migrateDone', { version: result.toVersion, count: result.documentCount }));
      onClose();
    },
  });

  const { reset: resetApply } = apply;

  // Будь-яка зміна вибору знецінює звіт — див. коментар до компонента.
  useEffect(() => {
    setReportState(null);
    resetApply();
  }, [targetId, mode, resetApply]);

  const options = (targets.data?.targets ?? []).map((v) => ({ value: String(v.id), label: v.version }));
  const busy = dryRun.isPending || apply.isPending;

  return (
    <Modal opened onClose={onClose} title={t('documents.migrateVersionTitle')} size="lg">
      <Stack gap="sm">
        {targets.data !== undefined && (
          <Text size="sm" data-migrate-current="">
            {t('documents.migrateCurrentVersion', { version: targets.data.currentVersion })}
          </Text>
        )}

        {targets.error !== null && <ErrorAlert error={targets.error} />}

        {targets.data !== undefined && options.length === 0 && (
          <Text size="sm" c="dimmed" data-migrate-no-targets="">
            {t('documents.migrateNoTargets')}
          </Text>
        )}

        <Select
          label={t('documents.migrateTarget')}
          data={options}
          value={targetId}
          onChange={setTargetId}
          disabled={options.length === 0 || busy}
          data-testid="migrate-target"
        />

        <SegmentedControl
          aria-label={t('documents.migrateVersionTitle')}
          value={mode}
          onChange={(value) => setMode(value as VersionMigrationMode)}
          disabled={busy}
          data={[
            { value: 'Safe', label: t('documents.migrateModeSafe') },
            { value: 'Presentation', label: t('documents.migrateModePresentation') },
          ]}
          data-testid="migrate-mode"
        />
        <Text size="xs" c="dimmed">
          {mode === 'Safe' ? t('documents.migrateModeSafeHint') : t('documents.migrateModePresentationHint')}
        </Text>

        {dryRun.error !== null && <ErrorAlert error={dryRun.error} />}

        {report !== null && <MigrationReport report={report} />}

        {apply.error !== null && <ErrorAlert error={apply.error} />}

        <Group justify="flex-end" mt="xs">
          <Button variant="default" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button
            variant="default"
            disabled={targetId === null || busy}
            loading={dryRun.isPending}
            onClick={() => dryRun.mutate(selectionKey)}
            data-testid="migrate-dry-run"
          >
            {t('documents.migrateDryRun')}
          </Button>
          <Button
            disabled={report === null || !report.canApply || busy}
            loading={apply.isPending}
            onClick={() => void whenEditsSaved(() => apply.mutate())}
            data-testid="migrate-apply"
          >
            {t('documents.migrateApply')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** Звіт сухого прогону: скільки документів, що переїде, що завадить. */
function MigrationReport({ report }: { readonly report: VersionMigrationReport }): JSX.Element {
  const shown = report.items.slice(0, ShownItems);
  const hidden = report.items.length - shown.length;

  return (
    <Stack gap="xs" data-migrate-report="">
      <Text size="sm" fw={500}>
        {t('documents.migrateDocuments', { count: report.documentCount })}
      </Text>
      <Text size="sm">
        {t('documents.migrateCounts', {
          transferred: report.transferredValues,
          lost: report.lostValues,
          guarded: report.guardedValues,
        })}
      </Text>

      {report.canApply ? (
        <Alert color="statusSuccess" data-migrate-can-apply="">
          {t('documents.migrateCanApply')}
        </Alert>
      ) : (
        <Alert color="statusError" data-migrate-refused="">
          <Stack gap="xs">
            {report.refusals.map((reason) => (
              <Text key={reason} size="sm">
                {refusalText(reason)}
              </Text>
            ))}
            {report.blockedGrantCount !== null && report.blockedGrantCount !== undefined && report.blockedGrantCount > 0 && (
              <Text size="sm" data-migrate-blocked-grants="">
                {t('documents.migrateGrantsNotMappedCount', { count: report.blockedGrantCount })}
              </Text>
            )}
          </Stack>
        </Alert>
      )}

      {shown.length > 0 && (
        <Table withTableBorder striped aria-label={t('documents.migrateVersionTitle')}>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('documents.migrateItemPath')}</Table.Th>
              <Table.Th>{t('documents.migrateItemKind')}</Table.Th>
              <Table.Th>{t('documents.migrateItemValues')}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {shown.map((item) => (
              <Table.Tr key={`${item.path}|${item.kind}|${item.field ?? ''}`}>
                <Table.Td>
                  {item.path}
                  {item.field !== null && item.field !== undefined && (
                    <Text span size="xs" c="dimmed">
                      {` ${item.field}: ${item.oldValue ?? '—'} → ${item.newValue ?? '—'}`}
                    </Text>
                  )}
                </Table.Td>
                <Table.Td>{itemKindText(item.kind)}</Table.Td>
                <Table.Td>{item.values}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}

      {(hidden > 0 || report.itemsTruncated) && (
        <Text size="xs" c="dimmed">
          {t('documents.migrateMoreItems', { count: hidden })}
        </Text>
      )}
    </Stack>
  );
}
