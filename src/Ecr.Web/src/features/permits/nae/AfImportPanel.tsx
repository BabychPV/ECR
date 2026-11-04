import { useState, type JSX } from 'react';
import { Alert, Button, Group, NativeSelect, Stack, Text, TextInput, Title } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { listDataSources } from '@/features/integration/dataSourceApi';
import { dataSourceName } from '@/features/integration/dataSourceName';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showApiError, showDone } from '@/shared/ui/notify';
import { importNaeFromAf, type AfImportIssue, type AfImportReport } from './afImportApi';

export interface AfImportPanelProps {
  readonly registryCode: string;
  readonly onClose: () => void;
}

/** Текст знахідки за її кодом: ключі - літерали в `t(...)` (сторож каталогу), невідомий код показується як є. */
function issueText(issue: AfImportIssue): string {
  switch (issue.code) {
    case 'noCode':
      return t('permits.nae.import.issue.noCode');
    case 'noRegion':
      return t('permits.nae.import.issue.noRegion');
    case 'duplicateBusinessKey':
      return t('permits.nae.import.issue.duplicateBusinessKey');
    case 'pathFlattened':
      return t('permits.nae.import.issue.pathFlattened');
    case 'existingElsewhere':
      return t('permits.nae.import.issue.existingElsewhere');
    case 'sourceIncomplete':
      return t('permits.nae.import.issue.sourceIncomplete');
    default:
      return issue.detail.length > 0 ? `${issue.code}: ${issue.detail}` : issue.code;
  }
}

function severityText(severity: string): string {
  if (severity === 'warning') return t('permits.nae.import.severity.warning');
  if (severity === 'skipped') return t('permits.nae.import.severity.skipped');

  return severity;
}

/** Є що застосовувати: нові вузли або довідник, якого ще немає. */
export function hasNewWork(report: AfImportReport): boolean {
  return report.registryProvisioned || report.levels.some((level) => level.create > 0);
}

/**
 * Завантаження структури NAE з PI AF: джерело й корінь -> «Перегляд» (dry run) -> «Завантажити».
 *
 * ⛔ Застосувати можна лише після перегляду з ТИМИ САМИМИ джерелом і коренем: зміна поля скидає перегляд, щоб людина
 * не застосувала не те, що бачила. Сервер створює лише відсутнє, тому повторне застосування безпечне.
 */
export function AfImportPanel({ registryCode, onClose }: AfImportPanelProps): JSX.Element {
  const queryClient = useQueryClient();
  const sources = useQuery({ queryKey: ['nae-af-sources'], queryFn: listDataSources, refetchOnWindowFocus: false });
  const [sourceId, setSourceId] = useState('');
  const [rootPath, setRootPath] = useState('');
  const [report, setReport] = useState<{ readonly inputs: string; readonly value: AfImportReport } | null>(null);

  const active = (sources.data ?? []).filter((source) => source.isActive);
  const chosen = sourceId === '' ? (active[0]?.id ?? null) : Number(sourceId);
  const inputs = `${String(chosen)}|${rootPath.trim()}`;

  const run = useMutation({
    mutationFn: (dryRun: boolean) =>
      importNaeFromAf({
        dataSourceId: chosen ?? 0,
        rootPath: rootPath.trim() === '' ? null : rootPath.trim(),
        registryCode,
        dryRun,
      }),
    onSuccess: async (value, dryRun) => {
      setReport({ inputs, value });
      if (!dryRun) {
        showDone(t('permits.nae.import.applied'));
        // Довідник міг з'явитися: визначення, дерево й рівні читаються заново.
        await queryClient.invalidateQueries({ queryKey: queryKeys.registries.all() });
      }
    },
    onError: (error: unknown) => showApiError(error),
  });
  const busy = usePendingLoading(run.isPending);

  const current = report !== null && report.inputs === inputs ? report.value : null;
  const previewed = current !== null && current.dryRun;
  const canApply = previewed && hasNewWork(current) && !run.isPending;

  return (
    <section aria-labelledby="nae-import-title" data-nae-import="">
      <Stack gap="sm">
        <Title order={2} size="h4" id="nae-import-title">
          {t('permits.nae.import.title')}
        </Title>
        <Text size="sm" c="dimmed">
          {t('permits.nae.import.description')}
        </Text>

        {sources.error !== null && sources.error !== undefined ? (
          <ErrorAlert error={sources.error} onRetry={() => void sources.refetch()} />
        ) : sources.isSuccess && active.length === 0 ? (
          <Text size="sm" role="status" data-nae-import-nosources="">
            {t('permits.nae.import.noSources')}
          </Text>
        ) : (
          <>
            <NativeSelect
              label={t('permits.nae.import.source')}
              value={chosen === null ? '' : String(chosen)}
              data={active.map((source) => ({ value: String(source.id), label: dataSourceName(source) }))}
              onChange={(event) => setSourceId(event.currentTarget.value)}
              disabled={sources.isPending}
            />
            <TextInput
              label={t('permits.nae.import.root')}
              description={t('permits.nae.import.rootHint')}
              value={rootPath}
              onChange={(event) => setRootPath(event.currentTarget.value)}
            />
          </>
        )}

        <Group gap="xs">
          <Button
            variant="default"
            loading={busy && run.variables === true}
            disabled={chosen === null || run.isPending}
            onClick={() => run.mutate(true)}
            data-nae-import-preview=""
          >
            {t('permits.nae.import.preview')}
          </Button>
          <Button
            loading={busy && run.variables === false}
            disabled={!canApply}
            onClick={() => run.mutate(false)}
            data-nae-import-apply=""
          >
            {t('permits.nae.import.apply')}
          </Button>
          <Button variant="subtle" onClick={onClose}>
            {t('permits.nae.import.close')}
          </Button>
        </Group>

        {current === null && report !== null && (
          <Text size="sm" c="dimmed" role="status">
            {t('permits.nae.import.stale')}
          </Text>
        )}
        {current !== null && <ReportView report={current} />}
      </Stack>
    </section>
  );
}

function ReportView({ report }: { readonly report: AfImportReport }): JSX.Element {
  const newWork = hasNewWork(report);

  return (
    <div data-nae-import-report={report.dryRun ? 'preview' : 'applied'} className="nae-import-report">
      <Text size="sm" fw={600} role="status">
        {report.dryRun
          ? t('permits.nae.import.previewDone')
          : report.applied
            ? t('permits.nae.import.applied')
            : t('permits.nae.import.nothingNew')}
      </Text>
      {report.dryRun && !newWork && (
        <Text size="sm" c="dimmed">
          {t('permits.nae.import.nothingNew')}
        </Text>
      )}
      {report.registryProvisioned && (
        <Text size="sm">{report.dryRun ? t('permits.nae.import.willProvision') : t('permits.nae.import.didProvision')}</Text>
      )}
      {!report.sourceComplete && (
        <Alert color="statusWarning" variant="light" role="alert" data-nae-import-incomplete="">
          {t('permits.nae.import.incomplete')}
        </Alert>
      )}
      <Text size="sm">{t('permits.nae.import.read', { count: report.elementsRead })}</Text>
      <table className="nae-used-table">
        <thead>
          <tr>
            <th scope="col">{t('permits.nae.import.level')}</th>
            <th scope="col">{t('permits.nae.import.create')}</th>
            <th scope="col">{t('permits.nae.import.existing')}</th>
          </tr>
        </thead>
        <tbody>
          {report.levels.map((level) => (
            <tr key={level.levelCode}>
              <td>{level.levelCode}</td>
              <td>{level.create}</td>
              <td>{level.existing}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {report.skipped > 0 && <Text size="sm">{t('permits.nae.import.skipped', { count: report.skipped })}</Text>}
      {report.issues.length > 0 && (
        <div>
          <Text size="sm" fw={600}>
            {t('permits.nae.import.issues')}
          </Text>
          <ul className="nae-import-issues">
            {report.issues.map((issue, index) => (
              <li key={`${issue.code}-${issue.path}-${String(index)}`}>
                <span>{severityText(issue.severity)}: </span>
                {issueText(issue)}
                {issue.path !== '' && <code className="nae-import-path"> {issue.path}</code>}
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}
