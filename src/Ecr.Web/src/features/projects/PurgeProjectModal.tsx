import { useEffect, useState, type JSX } from 'react';
import { Alert, Button, Code, Group, List, Loader, Modal, Stack, Text, Textarea, TextInput } from '@mantine/core';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { formatPeriodKey } from '@/shared/format/periodKey';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showApiError } from '@/shared/ui/notify';
import { usePurgePreview, usePurgeProject } from './purgeProject';

/** Найдовша причина — та сама межа, що на сервері (`PurgeProjectHandler.MaxReasonLength`). */
const MaxReasonLength = 500;

/** Місце коду в перекладі підпису: переклад може ставити `{code}` будь-де в реченні. */
const CodeMarker = '⁣';

/** Підпис звичайним текстом, у якому моноширинний лише сам код проєкту. */
function CodeLabel({ text, code }: { readonly text: string; readonly code: string }): JSX.Element {
  const [before, ...rest] = text.split(CodeMarker);
  if (rest.length === 0) return <>{text}</>;

  return (
    <>
      {before}
      <Code data-purge-code="">{code}</Code>
      {rest.join(code)}
    </>
  );
}

/**
 * ✎ 25.11: діалог «Видалити проєкт з усіма даними» (вкладка Lifecycle, лише `Project.DeleteWithData`).
 *
 * ⛔ Незворотне: перелік того, що зникне (документи з ключами, кількості комірок, результатів, проблем, погоджень,
 * місяці), попередження, ТОЧНО набраний код проєкту й обов'язкова причина. Архів (B69) чи залежні документи інших
 * проєктів (B80) — причина видима, кнопка недоступна (сервер однаково відмовить 409).
 */
export function PurgeProjectModal({
  projectId,
  code,
  opened,
  onClose,
  onDeleted,
}: {
  readonly projectId: number;
  readonly code: string;
  readonly opened: boolean;
  readonly onClose: () => void;
  readonly onDeleted: () => void;
}): JSX.Element {
  const preview = usePurgePreview(projectId, opened);
  const purge = usePurgeProject(projectId, onDeleted);
  // ФВ-14.26: спінер — лише коли видалення триває довше за поріг, а не з першого кадру.
  const purgingBusy = usePendingLoading(purge.isPending);
  const [typed, setTyped] = useState('');
  const [reason, setReason] = useState('');

  useEffect(() => {
    if (opened) {
      setTyped('');
      setReason('');
    }
  }, [opened]);

  useEffect(() => {
    if (purge.error !== null) showApiError(purge.error);
  }, [purge.error]);

  const data = preview.data;
  const blocked = data === undefined || data.blocker !== null;
  const ready = !blocked && typed === code && reason.trim().length > 0;

  return (
    <Modal opened={opened} onClose={onClose} title={t('projects.purge.title', { code })} size="lg">
      <Stack gap="sm" data-purge-dialog="">
        <Alert color="statusError" variant="light" data-purge-irreversible="">
          {t('projects.purge.irreversible')}
        </Alert>

        {preview.isPending && (
          <Group gap="xs">
            <Loader size="xs" />
            <Text size="sm">{t('projects.purge.loading')}</Text>
          </Group>
        )}
        {preview.error !== null && <ErrorAlert error={preview.error} onRetry={() => void preview.refetch()} />}

        {data !== undefined && (
          <>
            {data.archivedPeriodKeys.length > 0 || data.blocker === 'err.ECR-PRD-0409.purgeArchived' ? (
              <Alert color="statusWarning" data-purge-blocker="archived">
                {t('projects.purge.archived', {
                  periods: data.archivedPeriodKeys.map((k) => formatPeriodKey(k)).join(', ') || '—',
                })}
              </Alert>
            ) : null}

            {data.dependants.length > 0 && (
              <Alert color="statusWarning" data-purge-blocker="dependants">
                <Text size="sm" fw={500}>
                  {t('projects.purge.dependants')}
                </Text>
                <List size="sm">
                  {data.dependants.map((d) => (
                    <List.Item key={`${String(d.documentId)}-${d.reason}`}>
                      {t('projects.purge.dependant', { project: d.projectCode, key: d.businessKey, reason: d.reason })}
                    </List.Item>
                  ))}
                </List>
              </Alert>
            )}

            <Text size="sm" fw={600}>
              {t('projects.purge.documents', { count: data.documents.length })}
            </Text>
            {data.documents.length === 0 ? (
              <Text size="sm" c="dimmed">
                {t('projects.purge.noDocuments')}
              </Text>
            ) : (
              <List size="sm" data-purge-documents="">
                {data.documents.map((d) => (
                  <List.Item key={d.documentId}>
                    {t('projects.purge.documentCells', {
                      key: d.businessKey,
                      template: d.templateCode ?? '—',
                      cells: d.cells,
                    })}
                  </List.Item>
                ))}
              </List>
            )}
            <Text size="sm" data-purge-counts="">
              {t('projects.purge.counts', {
                cells: data.counts.cells,
                results: data.counts.calculationResults,
                runs: data.counts.calculationRuns,
                issues: data.counts.calculationIssues,
                events: data.counts.approvalEvents,
                states: data.counts.approvalStates,
                snapshots: data.counts.submissionSnapshots,
                periods: data.counts.periods,
              })}
            </Text>
            {data.dataMonths.length > 0 && (
              <Text size="sm" c="dimmed">
                {t('projects.purge.months', { months: data.dataMonths.map((k) => formatPeriodKey(k)).join(', ') })}
              </Text>
            )}
          </>
        )}

        <Textarea
          label={t('projects.purge.reason')}
          value={reason}
          onChange={(event) => setReason(event.currentTarget.value)}
          maxLength={MaxReasonLength}
          autosize
          minRows={2}
          required
          disabled={blocked}
        />
        <TextInput
          label={<CodeLabel text={t('projects.purge.typeCode', { code: CodeMarker })} code={code} />}
          value={typed}
          onChange={(event) => setTyped(event.currentTarget.value)}
          autoComplete="off"
          spellCheck={false}
          // ⚠ Моноширинний — лише саме поле вводу коду; `ff` на TextInput лягав і на підпис (увесь підпис моноширинним).
          styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)' } }}
          disabled={blocked}
          data-autofocus
        />
      </Stack>
      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={onClose}>
          {t('common.cancel')}
        </Button>
        <Button
          color="statusError"
          disabled={!ready}
          loading={purgingBusy}
          onClick={() => purge.mutate({ code: typed, reason: reason.trim() })}
        >
          {t('projects.purge.confirm')}
        </Button>
      </Group>
    </Modal>
  );
}
