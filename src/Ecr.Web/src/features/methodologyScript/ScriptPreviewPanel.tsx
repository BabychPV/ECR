import { Alert, Button, Group, Select, Stack, Text } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useMemo, useState, type JSX } from 'react';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { DocumentTableDto } from '@/api/types';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { listDocuments } from '@/features/documents/api';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { PeriodPicker } from '@/shared/ui/PeriodPicker';
import { showApiError } from '@/shared/ui/notify';
import { previewMethodologyScript } from './api';
import { ScriptPreviewTable } from './ScriptPreviewTable';
import { ScriptProblemsList } from './ScriptProblemsList';
import {
  buildPreviewRequest,
  defaultPeriodKey,
  singleTemplateCode,
} from './scriptPreviewRequest';
import { testFromPreviewRow, type TestCaseForm } from './testCases';
import type {
  ScriptOutlineDto,
  ScriptPreviewDto,
  ScriptPreviewRowDto,
  ScriptPreviewTableDto,
} from './types';

export interface ScriptPreviewPanelProps {
  readonly methodologyId: number;
  readonly versionId: number;
  /** Поточний текст редактора — саме його рахує перегляд. */
  readonly getText: () => string;
  readonly outline: ScriptOutlineDto | null;
  /** Код речовини → Id запису довідника (для очікувань по речовинах). */
  readonly substanceEntryIds: Readonly<Record<string, number>>;
  /** «Створити тест із рядка»: готова форма тесту. */
  readonly onCreateTest?: ((form: TestCaseForm) => void) | undefined;
  /** Обраний період повертається нагору: з нього прогоняється золотий набір. */
  readonly onPeriodChange?: (periodKey: number | null) => void;
  /** Клік по проблемі в блоці «текст має діагностики»: перейти до позиції в редакторі. */
  readonly onJump: (line: number, column: number) => void;
}

/**
 * Перегляд скрипта на документі: вибір документа й періоду, «Run» (поточний
 * текст редактора, нічого не записується), таблиця рядків зі станом і числами.
 */
export function ScriptPreviewPanel(props: ScriptPreviewPanelProps): JSX.Element {
  const { methodologyId, versionId, getText, outline, substanceEntryIds, onCreateTest } = props;

  const [documentId, setDocumentId] = useState<string | null>(null);
  const [periodKey, setPeriodKey] = useState<number | null>(null);
  const [tableInstanceId, setTableInstanceId] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [debounced] = useDebouncedValue(search.trim(), 250);
  const [result, setResult] = useState<ScriptPreviewDto | null>(null);

  const templateCode = singleTemplateCode(outline);

  const documents = useQuery({
    queryKey: queryKeys.methodologies.scriptDocuments(templateCode, debounced),
    queryFn: () =>
      listDocuments({
        periodKey: null,
        limit: 50,
        ...(debounced === '' ? {} : { q: debounced }),
        ...(templateCode === null ? {} : { templateCode }),
      }),
  });

  const documentOptions = useMemo(
    () =>
      (documents.data?.items ?? []).map((document) => {
        const name = localized(document.nameL10n);
        return {
          value: String(document.id),
          label: name === '' ? document.businessKey : `${document.businessKey} · ${name}`,
        };
      }),
    [documents.data],
  );

  const selectedId = documentId === null ? null : Number(documentId);

  const tables = useQuery({
    queryKey: queryKeys.methodologies.scriptDocumentTables(selectedId ?? 0, periodKey ?? 0),
    queryFn: () =>
      apiFetch<DocumentTableDto[]>(
        `/api/v1/documents/${String(selectedId)}/tables?periodKey=${String(periodKey)}`,
      ),
    enabled: selectedId !== null && periodKey !== null,
  });

  const tableOptions = useMemo(
    () =>
      (tables.data ?? [])
        .map((table) => ({
          value: String(table.tableInstanceId),
          label: table.tableCode,
        })),
    [tables.data],
  );

  const run = useMutation({
    mutationFn: () => {
      const request = buildPreviewRequest(getText(), {
        documentId: selectedId,
        periodKey,
        tableInstanceId: tableInstanceId === null ? null : Number(tableInstanceId),
      });
      if (request === null) return Promise.reject(new Error('preview selection is incomplete'));
      return previewMethodologyScript(methodologyId, versionId, request);
    },
    onSuccess: setResult,
    onError: showApiError,
  });
  const runLoading = usePendingLoading(run.isPending);

  const outputNames = useMemo(
    () => (outline?.outputs ?? []).map((output) => output.code),
    [outline],
  );

  const createTest =
    onCreateTest === undefined
      ? undefined
      : (row: ScriptPreviewRowDto, table: ScriptPreviewTableDto): void => {
          onCreateTest(
            testFromPreviewRow(row, {
              outline: outline ?? EmptyOutline,
              documentId: selectedId,
              periodKey: periodKey ?? 0,
              tableInstanceId: table.tableInstanceId,
              substanceEntryIds,
            }),
          );
        };

  return (
    <Stack gap="sm" data-testid="script-preview-panel">
      <Group gap="sm" align="flex-end" wrap="wrap">
        <Select
          label={t('methodologyScript.preview.document')}
          placeholder={t('methodologyScript.preview.documentPlaceholder')}
          data={documentOptions}
          value={documentId}
          searchable
          clearable
          nothingFoundMessage={t('methodologyScript.preview.noDocuments')}
          searchValue={search}
          onSearchChange={setSearch}
          filter={({ options }) => options}
          miw={260}
          onChange={(value) => {
            setDocumentId(value);
            setTableInstanceId(null);
            setResult(null);
            const found = documents.data?.items.find((item) => String(item.id) === value);
            const period = defaultPeriodKey(found?.periodStates);
            if (period !== null && periodKey === null) {
              setPeriodKey(period);
              props.onPeriodChange?.(period);
            }
          }}
        />

        <PeriodPicker
          label={t('methodologyScript.preview.period')}
          value={periodKey}
          onChange={(value) => {
            setPeriodKey(value);
            setTableInstanceId(null);
            props.onPeriodChange?.(value);
          }}
        />

        <Select
          label={t('methodologyScript.preview.tableInstance')}
          placeholder={t('methodologyScript.preview.allTables')}
          data={tableOptions}
          value={tableInstanceId}
          onChange={setTableInstanceId}
          clearable
          disabled={tableOptions.length === 0}
          miw={160}
        />

        <Button
          disabled={selectedId === null || periodKey === null}
          loading={runLoading}
          onClick={() => {
            if (run.isPending) return;
            run.mutate();
          }}
        >
          {t('methodologyScript.preview.run')}
        </Button>
      </Group>

      <Text size="xs" c="dimmed">
        {t('methodologyScript.preview.hint')}
      </Text>

      {result !== null && result.diagnostics.length > 0 && (
        <Alert color="statusWarning" title={t('methodologyScript.preview.blocked')}>
          <ScriptProblemsList diagnostics={result.diagnostics} onJump={props.onJump} />
        </Alert>
      )}

      {result !== null && result.diagnostics.length === 0 && (
        <ScriptPreviewTable
          preview={result}
          outputNames={outputNames}
          {...(createTest === undefined ? {} : { onCreateTest: createTest })}
        />
      )}
    </Stack>
  );
}

const EmptyOutline: ScriptOutlineDto = {
  tables: [],
  inputs: [],
  outputs: [],
  substances: [],
  imports: [],
  constants: [],
  functions: [],
  variables: [],
};
