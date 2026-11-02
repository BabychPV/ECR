import { useState, type JSX } from 'react';
import { Alert, Button, FileInput, Group, Stack, Text, Title } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import { EcrApiError } from '@/api/client';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { t } from '@/shared/i18n';
import { showApiError, showDone } from '@/shared/ui/notify';
import {
  applyNaeTreeImport,
  fetchNaeTreeTemplate,
  previewNaeTreeImport,
  type NaeTreeImportIssue,
  type NaeTreeImportReport,
} from './naeManualApi';

export interface NaeExcelImportPanelProps {
  readonly onClose: () => void;
}

const TemplateFileName = 'nae-tree-template.xlsx';

/** Текст знахідки за кодом: ключі - літерали в `t(...)` (сторож каталогу); невідомий код показується як є. */
function issueText(issue: NaeTreeImportIssue): string {
  switch (issue.code) {
    case 'headerNotFound':
      return t('permits.nae.manual.issue.headerNotFound');
    case 'regionRequired':
      return t('permits.nae.manual.issue.regionRequired');
    case 'siteRequired':
      return t('permits.nae.manual.issue.siteRequired');
    case 'badCode':
      return t('permits.nae.manual.issue.badCode');
    case 'areaMismatch':
      return t('permits.nae.manual.issue.areaMismatch');
    case 'duplicateInFile':
      return t('permits.nae.manual.issue.duplicateInFile');
    case 'existingElsewhere':
      return t('permits.nae.manual.issue.existingElsewhere');
    case 'codeTaken':
      return t('permits.nae.manual.issue.codeTaken');
    case 'nameDiffers':
      return t('permits.nae.manual.issue.nameDiffers');
    default:
      return issue.detail.length > 0 ? `${issue.code}: ${issue.detail}` : issue.code;
  }
}

const PlanChangedKey = 'err.ECR-IMP-0422.regPlanChanged';

function isPlanChanged(error: unknown): boolean {
  return error instanceof EcrApiError && error.problem.extensions2?.['messageKey'] === PlanChangedKey;
}

/** Є що застосовувати: нові вузли (рівні з `create > 0`) і жодної помилки. */
export function canApplyReport(report: NaeTreeImportReport): boolean {
  return report.canApply && report.levels.some((level) => level.create > 0);
}

/** Віддає blob браузеру як завантаження (URL звільняється одразу після кліку). */
function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);

  try {
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    anchor.style.display = 'none';
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
  } finally {
    URL.revokeObjectURL(url);
  }
}

/**
 * Імпорт дерева НВ з Excel (без AF): шаблон -> файл -> «Перегляд» (нічого не пишеться) -> «Застосувати» (все або нічого).
 *
 * ⛔ Застосувати можна лише ту саму пару «файл + відбиток перегляду»: інший файл скидає перегляд, а відбиток
 * (`fingerprint`) іде на сервер як `expectedFingerprint` - якщо дерево змінилося після перегляду, сервер відмовить і
 * перегляд доведеться повторити (а не застосувати не те, що бачила людина).
 */
export function NaeExcelImportPanel({ onClose }: NaeExcelImportPanelProps): JSX.Element {
  const queryClient = useQueryClient();
  const [file, setFile] = useState<File | null>(null);
  const [report, setReport] = useState<{ readonly file: File; readonly value: NaeTreeImportReport } | null>(null);
  const [stale, setStale] = useState(false);

  const preview = useMutation({
    mutationFn: (chosen: File) => previewNaeTreeImport(chosen),
    onSuccess: (value, chosen) => {
      setStale(false);
      setReport({ file: chosen, value });
    },
    onError: (error: unknown) => showApiError(error),
  });
  const apply = useMutation({
    mutationFn: (args: { readonly file: File; readonly fingerprint: string }) => applyNaeTreeImport(args.file, args.fingerprint),
    onSuccess: async (value, args) => {
      setReport({ file: args.file, value });
      showDone(value.applied ? t('permits.nae.manual.excel.applied') : t('permits.nae.manual.excel.nothingNew'));
      await queryClient.invalidateQueries({ queryKey: queryKeys.registries.all() });
    },
    onError: (error: unknown) => {
      // Відбиток не збігся (дерево чи файл змінилися): 422 ECR-IMP-0422 з ключем regPlanChanged - перегляд застарів.
      // ⛔ Лише за цим ключем: інші 422 застосування (напр. ECR-REG-0422) показують власний текст сервера.
      if (isPlanChanged(error)) {
        setReport(null);
        setStale(true);

        return;
      }
      showApiError(error);
    },
  });
  const template = useMutation({
    mutationFn: fetchNaeTreeTemplate,
    onSuccess: (blob) => saveBlob(blob, TemplateFileName),
    onError: (error: unknown) => showApiError(error),
  });
  const busyPreview = usePendingLoading(preview.isPending);
  const busyApply = usePendingLoading(apply.isPending);
  const busyTemplate = usePendingLoading(template.isPending);

  const current = report !== null && report.file === file ? report.value : null;
  const previewed = current !== null && !current.applied;
  const canApply = previewed && canApplyReport(current) && !apply.isPending;

  return (
    <section aria-labelledby="nae-excel-title" data-nae-excel="">
      <Stack gap="sm">
        <Title order={2} size="h4" id="nae-excel-title">
          {t('permits.nae.manual.excel.title')}
        </Title>
        <Text size="sm" c="dimmed">
          {t('permits.nae.manual.excel.description')}
        </Text>
        <Group gap="xs">
          <Button
            variant="default"
            size="xs"
            loading={busyTemplate}
            onClick={() => template.mutate()}
            data-nae-excel-template=""
          >
            {t('permits.nae.manual.excel.template')}
          </Button>
        </Group>
        <FileInput
          label={t('permits.nae.manual.excel.file')}
          accept=".xlsx"
          value={file}
          onChange={(chosen) => {
            setFile(chosen);
            setStale(false);
          }}
          data-nae-excel-file=""
        />
        <Group gap="xs">
          <Button
            variant="default"
            loading={busyPreview}
            disabled={file === null || preview.isPending || apply.isPending}
            onClick={() => file !== null && preview.mutate(file)}
            data-nae-excel-preview=""
          >
            {t('permits.nae.manual.excel.preview')}
          </Button>
          <Button
            loading={busyApply}
            disabled={!canApply}
            onClick={() => {
              if (file !== null && current !== null) apply.mutate({ file, fingerprint: current.fingerprint });
            }}
            data-nae-excel-apply=""
          >
            {t('permits.nae.manual.excel.apply')}
          </Button>
          <Button variant="subtle" onClick={onClose}>
            {t('permits.nae.manual.excel.close')}
          </Button>
        </Group>

        {stale && (
          <Alert color="statusWarning" variant="light" role="alert" data-nae-excel-stale="">
            {t('permits.nae.manual.excel.stale')}
          </Alert>
        )}
        {current !== null && <ReportView report={current} />}
      </Stack>
    </section>
  );
}

function ReportView({ report }: { readonly report: NaeTreeImportReport }): JSX.Element {
  const errors = report.issues.filter((issue) => issue.severity === 'error');
  const warnings = report.issues.filter((issue) => issue.severity !== 'error');
  const newWork = canApplyReport(report);

  return (
    <div data-nae-excel-report={report.applied ? 'applied' : 'preview'} className="nae-import-report">
      <Text size="sm" fw={600} role="status">
        {report.applied
          ? t('permits.nae.manual.excel.applied')
          : report.canApply
            ? t('permits.nae.manual.excel.previewDone')
            : t('permits.nae.manual.excel.hasErrors')}
      </Text>
      {!report.applied && report.canApply && !newWork && (
        <Text size="sm" c="dimmed">
          {t('permits.nae.manual.excel.nothingNew')}
        </Text>
      )}
      {report.registryProvisioned && <Text size="sm">{t('permits.nae.manual.excel.didProvision')}</Text>}
      <Text size="sm">{t('permits.nae.manual.excel.rows', { count: report.rowsRead })}</Text>
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
      <IssueList title={t('permits.nae.manual.excel.errors')} issues={errors} kind="error" />
      <IssueList title={t('permits.nae.manual.excel.warnings')} issues={warnings} kind="warning" />
      {report.droppedIssues > 0 && (
        <Text size="sm" c="dimmed">
          {t('permits.nae.manual.excel.dropped', { count: report.droppedIssues })}
        </Text>
      )}
    </div>
  );
}

function IssueList({
  title,
  issues,
  kind,
}: {
  readonly title: string;
  readonly issues: readonly NaeTreeImportIssue[];
  readonly kind: 'error' | 'warning';
}): JSX.Element | null {
  if (issues.length === 0) return null;

  return (
    <div data-nae-excel-issues={kind}>
      <Text size="sm" fw={600}>
        {title}
      </Text>
      <ul className="nae-import-issues">
        {issues.map((issue, index) => (
          <li key={`${issue.code}-${String(issue.row)}-${String(index)}`}>
            {issue.row > 0 && <span>{t('permits.nae.manual.excel.row', { row: issue.row })}: </span>}
            {issueText(issue)}
            {issue.naeCode !== null && <code className="nae-import-path"> {issue.naeCode}</code>}
          </li>
        ))}
      </ul>
    </div>
  );
}
