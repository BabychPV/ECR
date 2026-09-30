import { useState, type JSX } from 'react';
import { Alert, Button, FileInput, Group, Modal, Stack, Table, Text } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import { t } from '@/shared/i18n';
import { showApiError, showDone } from '@/shared/ui/notify';
import { importMethodologyPackage, type MethodologyImportReportDto } from './api';

type Issue = MethodologyImportReportDto['blockers'][number];

/**
 * Звіт із відмови запису (422 — блокери, 409 — конфлікти): сервер кладе його в
 * `report`, у корені problem+json або в `extensions2`.
 */
function reportFromError(error: unknown): MethodologyImportReportDto | null {
  if (!(error instanceof EcrApiError)) return null;

  const report =
    error.problem.extensions2?.['report'] ?? (error.problem as unknown as Record<string, unknown>)['report'];

  return typeof report === 'object' && report !== null ? (report as MethodologyImportReportDto) : null;
}

/** Текст файлу через `FileReader` — однаково в браузері й у jsdom тестів. */
function readText(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(typeof reader.result === 'string' ? reader.result : '');
    reader.onerror = () => reject(reader.error ?? new Error('read failed'));
    reader.readAsText(file);
  });
}

function outcomeLabel(outcome: string): string {
  switch (outcome) {
    case 'created':
      return t('methodologies.importOutcomeCreated');
    case 'unchanged':
      return t('methodologies.importOutcomeUnchanged');
    case 'blocked':
      return t('methodologies.importOutcomeBlocked');
    default:
      return t('methodologies.importOutcomeConflict');
  }
}

function IssueTable({ title, issues }: { title: string; issues: Issue[] }): JSX.Element | null {
  if (issues.length === 0) return null;

  return (
    <>
      <Text fw={600}>
        {title} ({issues.length})
      </Text>
      <Table striped withTableBorder>
        <Table.Tbody>
          {issues.slice(0, 200).map((issue, index) => (
            <Table.Tr key={`${issue.kind}:${String(index)}`}>
              <Table.Td>{issue.kind}</Table.Td>
              <Table.Td>{[issue.methodology, issue.version, issue.subject].filter(Boolean).join(' / ')}</Table.Td>
              <Table.Td>{issue.detail}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </>
  );
}

/**
 * Кнопка й діалог «Імпорт пакета» методологій з AF (крок V, FEATURE-HSE301-VIEW §11.6).
 *
 * ⛔ Спершу — перевірка (сухий прогін): вона нічого не пише і показує, що буде
 * створено, блокери й конфлікти. «Імпортувати» доступне лише тоді, коли
 * перевірка сказала `created`. Імпорт створює тільки ЧЕРНЕТКИ — публікація
 * лишається окремою дією іншої людини (чотири ока, `D-40`).
 */
export function MethodologyPackageImport(): JSX.Element {
  const queryClient = useQueryClient();
  const [opened, setOpened] = useState(false);
  const [file, setFile] = useState<File | null>(null);
  const [pkg, setPkg] = useState<unknown>(null);
  const [report, setReport] = useState<MethodologyImportReportDto | null>(null);
  const [invalid, setInvalid] = useState(false);

  const check = useMutation({
    mutationFn: (value: unknown) => importMethodologyPackage(value, true),
    onSuccess: setReport,
    onError: showApiError,
  });

  const apply = useMutation({
    mutationFn: (value: unknown) => importMethodologyPackage(value, false),
    onSuccess: async (done) => {
      setReport(done);
      await queryClient.invalidateQueries({ queryKey: queryKeys.methodologies.list() });
      showDone(
        done.applied
          ? t('methodologies.importDone', { versions: done.totals.versionsToCreate })
          : t('methodologies.importOutcomeUnchanged'),
      );
    },
    onError: (error: unknown) => {
      const refused = reportFromError(error);
      if (refused !== null) setReport(refused);
      showApiError(error);
    },
  });

  async function choose(next: File | null): Promise<void> {
    setFile(next);
    setReport(null);
    setPkg(null);
    setInvalid(false);
    if (next === null) return;

    try {
      setPkg(JSON.parse(await readText(next)) as unknown);
    } catch {
      setInvalid(true);
    }
  }

  return (
    <>
      <Button variant="default" onClick={() => setOpened(true)}>
        {t('methodologies.importPackage')}
      </Button>

      <Modal opened={opened} onClose={() => setOpened(false)} title={t('methodologies.importTitle')} size="xl">
        <Stack gap="sm">
          <Text size="sm" c="dimmed">
            {t('methodologies.importHint')}
          </Text>

          <FileInput
            label={t('methodologies.importFile')}
            accept="application/json,.json"
            value={file}
            onChange={(next) => void choose(next)}
            error={invalid ? t('methodologies.importInvalidFile') : undefined}
            clearable
          />

          <Group>
            <Button
              variant="default"
              disabled={pkg === null}
              loading={check.isPending}
              onClick={() => check.mutate(pkg)}
            >
              {t('methodologies.importCheck')}
            </Button>
            <Button
              disabled={pkg === null || report?.dryRun !== true || report.outcome !== 'created'}
              loading={apply.isPending}
              onClick={() => apply.mutate(pkg)}
            >
              {t('methodologies.importApply')}
            </Button>
          </Group>

          {report !== null && (
            <Stack gap="xs">
              <Alert
                color={report.outcome === 'created' || report.outcome === 'unchanged' ? 'statusSuccess' : 'statusError'}
                title={outcomeLabel(report.outcome)}
              >
                {t('methodologies.importTotals', {
                  methodologies: report.totals.methodologiesToCreate,
                  versions: report.totals.versionsToCreate,
                  unchanged: report.totals.versionsUnchanged,
                  formulas: report.totals.formulas,
                  constants: report.totals.constants,
                  imports: report.totals.imports,
                })}
              </Alert>

              <IssueTable title={t('methodologies.importBlockers')} issues={report.blockers} />
              <IssueTable title={t('methodologies.importConflicts')} issues={report.conflicts} />
              <IssueTable title={t('methodologies.importWarnings')} issues={report.warnings} />
            </Stack>
          )}
        </Stack>
      </Modal>
    </>
  );
}
