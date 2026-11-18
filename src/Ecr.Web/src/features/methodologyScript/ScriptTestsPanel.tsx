import {
  ActionIcon,
  Alert,
  Button,
  Group,
  Select,
  Stack,
  Table,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState, type JSX } from 'react';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import { scriptKeys } from './scriptQueryKeys';
import type { DocumentSummary, MethodologyTestCaseDto } from '@/api/types';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { TestCaseRunner } from '@/features/expressions/TestCaseRunner';
import { methodologyTestCases, saveMethodologyTestCase } from '@/features/methodologies/api';
import { t } from '@/shared/i18n';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PeriodPicker } from '@/shared/ui/PeriodPicker';
import { ResizableModal } from '@/shared/ui/ResizableModal';
import { showApiError, showDone } from '@/shared/ui/notify';
import {
  expectedRowsOf,
  parseValueText,
  suggestTestCode,
  testFormToJson,
  validateTestForm,
  valueText,
  type ExpectedRow,
  type TestFormIssue,
} from './testForm';
import { parseTestCase, type TestCaseForm, type TestInputField } from './testCases';
import type { MethodologySubstancesDto, ScriptOutlineDto } from './types';

/** Тест, який зараз правлять або створюють. */
export interface ScriptTestEditing {
  readonly code: string;
  readonly isNew: boolean;
  readonly form: TestCaseForm;
}

export interface ScriptTestsPanelProps {
  readonly methodologyId: number;
  readonly versionId: number;
  readonly editable: boolean;
  readonly outline: ScriptOutlineDto | null;
  readonly substances: MethodologySubstancesDto | undefined;
  /** Період для «Прогнати набір»: з перегляду, інакше — період першого тесту. */
  readonly periodKey: number | null;
  readonly editing: ScriptTestEditing | null;
  readonly onEditingChange: (editing: ScriptTestEditing | null) => void;
}

function issueText(issue: TestFormIssue): string {
  return t(`methodologyScript.tests.issue.${issue}`);
}

/** Підпис джерела тесту: документ за бізнес-ключем, а не за Id. */
function SourceLine({ form }: { readonly form: TestCaseForm }): JSX.Element | null {
  const documentId = form.documentId;
  const document = useQuery({
    queryKey: scriptKeys.document(documentId ?? 0, form.period),
    queryFn: () =>
      apiFetch<DocumentSummary>(`/api/v1/documents/${String(documentId)}?periodKey=${String(form.period)}`),
    enabled: documentId !== null && form.period > 0,
  });

  if (documentId === null) return null;

  return (
    <Text size="sm" c="dimmed" data-testid="script-test-source">
      {t('methodologyScript.tests.source', {
        document: document.data?.businessKey ?? '…',
        row: form.rowKey ?? '—',
      })}
    </Text>
  );
}

/**
 * Поле значення входу тесту. ⛔ Текст, який друкує людина, не підміняється канонічним виглядом значення: інакше
 * `1.0` ставало `1` посеред набору, і `1.05` неможливо було набрати (виходило `15`).
 */
function ValueTextInput(props: {
  readonly value: number | string | null;
  readonly onValue: (value: number | string | null) => void;
}): JSX.Element {
  const [text, setText] = useState(valueText(props.value));
  const shown = parseValueText(text) === props.value ? text : valueText(props.value);

  return (
    <TextInput
      aria-label={t('methodologyScript.tests.value')}
      value={shown}
      onChange={(event) => {
        const next = event.currentTarget.value;
        setText(next);
        props.onValue(parseValueText(next));
      }}
    />
  );
}

export function TestFormBody(props: {
  readonly editing: ScriptTestEditing;
  readonly existingCodes: readonly string[];
  readonly outline: ScriptOutlineDto | null;
  readonly substances: MethodologySubstancesDto | undefined;
  readonly saving: boolean;
  readonly onSave: (code: string, form: TestCaseForm, rows: readonly ExpectedRow[]) => void;
}): JSX.Element {
  const { editing, existingCodes, outline, substances, saving, onSave } = props;
  const [code, setCode] = useState(editing.code);
  const [period, setPeriod] = useState<number | null>(editing.form.period > 0 ? editing.form.period : null);
  const [inputs, setInputs] = useState<readonly TestInputField[]>(editing.form.inputs);
  const [rows, setRows] = useState<readonly ExpectedRow[]>(expectedRowsOf(editing.form.expected));
  const [tolerance, setTolerance] = useState(editing.form.tolerance);

  const issues = validateTestForm({
    code,
    isNew: editing.isNew,
    existingCodes,
    period: period ?? 0,
    expectedRows: rows,
    tolerance,
  });

  const outputOptions = (outline?.outputs ?? []).map((output) => ({
    value: output.code,
    label: output.code,
  }));
  const substanceOptions = (substances?.items ?? [])
    .filter((item) => !item.isDeleted)
    .map((item) => ({ value: String(item.substanceEntryId), label: item.code }));

  const setInput = (index: number, patch: Partial<TestInputField>): void =>
    setInputs(inputs.map((input, i) => (i === index ? { ...input, ...patch } : input)));
  const setRow = (index: number, patch: Partial<ExpectedRow>): void =>
    setRows(rows.map((row, i) => (i === index ? { ...row, ...patch } : row)));

  return (
    <Stack gap="md">
      <Group align="flex-end" gap="sm" wrap="wrap">
        <TextInput
          label={t('methodologies.code')}
          description={t('methodologyScript.tests.codeHint')}
          value={code}
          disabled={!editing.isNew}
          onChange={(event) => setCode(event.currentTarget.value)}
          data-autofocus
        />
        <PeriodPicker
          label={t('methodologyScript.preview.period')}
          value={period}
          onChange={setPeriod}
        />
      </Group>

      <SourceLine form={{ ...editing.form, period: period ?? 0 }} />

      <Stack gap="xs">
        <Title order={5}>{t('methodologyScript.tests.inputs')}</Title>
        <Text size="xs" c="dimmed">
          {t('methodologyScript.tests.inputsHint')}
        </Text>
        {inputs.length > 0 && (
          <Table withRowBorders={false} verticalSpacing={2} aria-label={t('methodologyScript.tests.inputs')}>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('methodologyScript.tests.field')}</Table.Th>
                <Table.Th>{t('methodologyScript.tests.value')}</Table.Th>
                <Table.Th w={40} />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {inputs.map((input, index) => (
                // ⚠ Ключ — лише індекс: з іменем у ключі поле імені перемонтовувалося на кожен символ і губило фокус.
                <Table.Tr key={index}>
                  <Table.Td>
                    <TextInput
                      aria-label={t('methodologyScript.tests.field')}
                      value={input.name}
                      onChange={(event) => setInput(index, { name: event.currentTarget.value })}
                    />
                  </Table.Td>
                  <Table.Td>
                    <ValueTextInput
                      value={input.value}
                      onValue={(value) => setInput(index, { value })}
                    />
                  </Table.Td>
                  <Table.Td>
                    <ActionIcon
                      variant="subtle"
                      color="statusError"
                      aria-label={t('methodologyScript.tests.removeRow')}
                      onClick={() => setInputs(inputs.filter((_, i) => i !== index))}
                    >
                      ×
                    </ActionIcon>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
        <Group>
          <Button
            size="xs"
            variant="default"
            onClick={() => setInputs([...inputs, { name: '', value: null }])}
          >
            {t('methodologyScript.tests.addInput')}
          </Button>
        </Group>
      </Stack>

      <Stack gap="xs">
        <Title order={5}>{t('methodologyScript.tests.expected')}</Title>
        {rows.length > 0 && (
          <Table withRowBorders={false} verticalSpacing={2} aria-label={t('methodologyScript.tests.expected')}>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('methodologyScript.tests.output')}</Table.Th>
                <Table.Th>{t('methodologyScript.tests.substance')}</Table.Th>
                <Table.Th>{t('methodologyScript.tests.expectedValue')}</Table.Th>
                <Table.Th w={40} />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {rows.map((row, index) => (
                <Table.Tr key={index}>
                  <Table.Td>
                    <Select
                      aria-label={t('methodologyScript.tests.output')}
                      data={
                        outputOptions.some((option) => option.value === row.output) || row.output === ''
                          ? outputOptions
                          : [...outputOptions, { value: row.output, label: row.output }]
                      }
                      value={row.output === '' ? null : row.output}
                      searchable
                      onChange={(value) => setRow(index, { output: value ?? '' })}
                    />
                  </Table.Td>
                  <Table.Td>
                    <Select
                      aria-label={t('methodologyScript.tests.substance')}
                      data={substanceOptions}
                      value={row.substanceEntryId === null ? null : String(row.substanceEntryId)}
                      clearable
                      disabled={substanceOptions.length === 0 && row.substanceEntryId === null}
                      onChange={(value) =>
                        setRow(index, { substanceEntryId: value === null ? null : Number(value) })
                      }
                    />
                  </Table.Td>
                  <Table.Td>
                    <TextInput
                      aria-label={t('methodologyScript.tests.expectedValue')}
                      inputMode="decimal"
                      value={row.text}
                      onChange={(event) => setRow(index, { text: event.currentTarget.value })}
                    />
                  </Table.Td>
                  <Table.Td>
                    <ActionIcon
                      variant="subtle"
                      color="statusError"
                      aria-label={t('methodologyScript.tests.removeRow')}
                      onClick={() => setRows(rows.filter((_, i) => i !== index))}
                    >
                      ×
                    </ActionIcon>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
        <Group>
          <Button
            size="xs"
            variant="default"
            onClick={() =>
              setRows([
                ...rows,
                { output: outputOptions[0]?.value ?? '', substanceEntryId: null, text: '' },
              ])
            }
          >
            {t('methodologyScript.tests.addExpected')}
          </Button>
        </Group>
      </Stack>

      {/* ⛔ `TextInput`, а не `NumberInput`: допуск — `decimal` контракту, знаки мають дійти до сервера як надруковані. */}
      <TextInput
        label={t('methodologies.tolerance')}
        description={t('methodologies.toleranceHint')}
        inputMode="decimal"
        value={tolerance}
        onChange={(event) => setTolerance(event.currentTarget.value)}
      />

      {issues.length > 0 && (
        <Alert color="statusWarning" data-testid="script-test-issues">
          <Stack gap="xs">
            {issues.map((issue) => (
              <Text key={issue} size="sm">
                {issueText(issue)}
              </Text>
            ))}
          </Stack>
        </Alert>
      )}

      <Button
        disabled={issues.length > 0}
        loading={saving}
        onClick={() =>
          onSave(code.trim(), { ...editing.form, period: period ?? 0, inputs, tolerance }, rows)
        }
      >
        {t('methodologies.save')}
      </Button>
    </Stack>
  );
}

/**
 * Золотий набір скрипта: перелік тестів, структурована форма (поле → значення,
 * вихід → очікування, допуск) і прогін набору. Жодного JSON на екрані: форма
 * серіалізується в наявні `inputJson`/`expectedJson` (`testCases.ts`).
 */
export function ScriptTestsPanel(props: ScriptTestsPanelProps): JSX.Element {
  const { methodologyId, versionId, editable, outline, substances, editing, onEditingChange } = props;
  const queryClient = useQueryClient();

  const tests = useQuery({
    queryKey: queryKeys.methodologies.tests(versionId),
    queryFn: () => methodologyTestCases(methodologyId, versionId),
  });

  const existingCodes = useMemo(() => (tests.data ?? []).map((test) => test.code), [tests.data]);

  const save = useMutation({
    mutationFn: (args: { code: string; form: TestCaseForm; rows: readonly ExpectedRow[] }) =>
      saveMethodologyTestCase(
        methodologyId,
        versionId,
        args.code,
        testFormToJson(args.form, args.rows),
      ),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.methodologies.tests(versionId) });
      onEditingChange(null);
      showDone(t('methodologies.testSaved'));
    },
    onError: showApiError,
  });
  const saveLoading = usePendingLoading(save.isPending);

  const startEditing = (test: MethodologyTestCaseDto): void => {
    const form = parseTestCase(test.inputJson, test.expectedJson, test.tolerance);
    if (form === null) return;
    onEditingChange({ code: test.code, isNew: false, form });
  };

  const runnerPeriod =
    props.periodKey ??
    (() => {
      for (const test of tests.data ?? []) {
        const form = parseTestCase(test.inputJson, test.expectedJson, test.tolerance);
        if (form !== null && form.period > 0) return form.period;
      }
      return null;
    })();

  return (
    <Stack gap="md" data-testid="script-tests">
      <Group justify="space-between">
        <Title order={5}>{t('methodologies.tests')}</Title>
        {editable && (
          <Button
            size="xs"
            variant="default"
            onClick={() =>
              onEditingChange({
                code: suggestTestCode(existingCodes),
                isNew: true,
                form: {
                  period: props.periodKey ?? 0,
                  documentId: null,
                  tableInstanceId: null,
                  rowKey: null,
                  inputs: [],
                  expected: [],
                  tolerance: '0.0001',
                },
              })
            }
          >
            {t('methodologies.addTest')}
          </Button>
        )}
      </Group>

      <AsyncBoundary<MethodologyTestCaseDto[]>
        isPending={tests.isPending}
        error={tests.error}
        data={tests.data}
        isEmpty={(list) => list.length === 0}
        emptyTitle={t('methodologies.noTests')}
        emptyHint={t('methodologies.noTestsHint')}
        skeleton="table"
        onRetry={() => void tests.refetch()}
      >
        {(list) => (
          <Table striped withTableBorder>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('methodologies.testCode')}</Table.Th>
                <Table.Th>{t('methodologyScript.tests.expected')}</Table.Th>
                <Table.Th>{t('methodologies.tolerance')}</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {list.map((test) => {
                const form = parseTestCase(test.inputJson, test.expectedJson, test.tolerance);
                return (
                  <Table.Tr key={test.id}>
                    <Table.Td>{test.code}</Table.Td>
                    <Table.Td>
                      {form === null
                        ? '—'
                        : form.expected
                            .map((item) => `${item.output} = ${String(item.value)}`)
                            .join(', ')}
                    </Table.Td>
                    <Table.Td>{test.tolerance}</Table.Td>
                    <Table.Td>
                      {editable && form !== null && (
                        <Button size="xs" variant="subtle" onClick={() => startEditing(test)}>
                          {t('methodologies.editFormula')}
                        </Button>
                      )}
                    </Table.Td>
                  </Table.Tr>
                );
              })}
            </Table.Tbody>
          </Table>
        )}
      </AsyncBoundary>

      {runnerPeriod === null ? (
        <Text size="sm" c="dimmed">
          {t('methodologyScript.tests.runNeedsPeriod')}
        </Text>
      ) : (
        <TestCaseRunner
          methodologyId={methodologyId}
          methodologyVersionId={versionId}
          periodKey={runnerPeriod}
        />
      )}

      <ResizableModal
        sizeKey="methodology.scriptTest"
        opened={editing !== null}
        onClose={() => onEditingChange(null)}
        title={t('methodologies.tests')}
        size="lg"
      >
        {editing !== null && (
          <TestFormBody
            // ⚠ `key`: форма — локальний стан, і новий тест не успадковує чужі рядки.
            key={`${editing.isNew ? 'new' : 'edit'}:${editing.code}:${String(editing.form.rowKey)}`}
            editing={editing}
            existingCodes={existingCodes}
            outline={outline}
            substances={substances}
            saving={saveLoading}
            onSave={(code, form, rows) => {
              if (save.isPending) return;
              save.mutate({ code, form, rows });
            }}
          />
        )}
      </ResizableModal>
    </Stack>
  );
}
