import { useState, type JSX } from 'react';
import {
  Button,
  Checkbox,
  Group,
  Modal,
  NumberInput,
  Select,
  Stack,
  Table,
  Text,
  Textarea,
  TextInput,
} from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type {
  CalculationBindingDto,
  MethodologyConstantDto,
  MethodologyDraftVersionDto,
  MethodologyOutputDto,
  MethodologyRuleDto,
  MethodologyTestCaseDto,
  UnitRef,
} from '@/api/types';
import { apiFetch } from '@/api/client';
import { t } from '@/shared/i18n';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { showApiError, showDone } from '@/shared/ui/notify';
import {
  calculationBindings,
  methodologyConstants,
  methodologyOutputs,
  methodologyRules,
  methodologyTestCases,
  saveCalculationBinding,
  saveMethodologyConstant,
  saveMethodologyModes,
  saveMethodologyOutput,
  saveMethodologyRule,
  saveMethodologyTestCase,
} from './api';

/**
 * Вміст версії методології, якого доти не було чим ані завести, ані побачити
 * (директива №09, `W6`).
 *
 * ⛔ Панелі стоять поруч із формулами навмисно, а не в окремих екранах.
 * Методологія не рахує НІЧОГО, поки в неї немає всіх п'яти складників:
 * формула без константи дає нуль, формула без оголошеного виходу не
 * записується взагалі, версія без правила відбору не зачіпає жодного рядка
 * документа, версія без золотого набору не публікується (`ФВ-9.12`), а без
 * прив'язки до колонки перерахунок завершується успіхом і не рахує нічого.
 * Розкидані по вкладках, вони виглядали б як необов'язкові подробиці.
 */

/** Що спільне в кожної панелі вмісту версії. */
interface PanelProps {
  /** Методологія-контейнер. */
  readonly methodologyId: number;

  /** Версія, вміст якої показуємо. */
  readonly versionId: number;

  /** Чи можна правити: чернетка **і** право (`mayEditContent`). */
  readonly editable: boolean;
}

/** Константа, яку зараз правлять у діалозі. */
interface ConstantDraft {
  readonly code: string;
  readonly kind: 'Numeric' | 'Text' | 'CategoryLabel';
  readonly value: string;
  readonly textValue: string;
  readonly unitId: number | null;
  readonly validFrom: string;
  readonly validTo: string;
  readonly category: string;
  readonly source: string;
  readonly isNew: boolean;
}

/** Порожня константа для нового запису. */
const emptyConstant: ConstantDraft = {
  code: '',
  kind: 'Numeric',
  value: '',
  textValue: '',
  unitId: null,
  validFrom: '',
  validTo: '',
  category: '',
  source: '',
  isNew: true,
};

/**
 * Константи версії (`ФВ-16.1`, `ФВ-16.5`).
 *
 * ⛔ Константа не завжди число: з 6507 констант корпусу 108 нечислові, і
 * близько 90 із них ужиті у виразах операндом порівняння. Тому вид обирається
 * явно, а не вгадується за тим, чи розібралося значення.
 */
export function MethodologyConstantsPanel({
  methodologyId,
  versionId,
  editable,
}: PanelProps): JSX.Element {
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState<ConstantDraft | null>(null);

  const constants = useQuery({
    queryKey: ['methodology-constants', versionId],
    queryFn: () => methodologyConstants(methodologyId, versionId),
  });

  const units = useQuery({
    queryKey: ['units'],
    queryFn: () => apiFetch<UnitRef[]>('/api/v1/units'),
    staleTime: 60 * 60 * 1000,
  });

  const save = useMutation({
    mutationFn: (draft: ConstantDraft) =>
      saveMethodologyConstant(methodologyId, versionId, draft.code, {
        kind: draft.kind,
        // ⛔ Число і текст ідуть ВЗАЄМОВИКЛЮЧНО. Лишити старе значення другого
        // поля означало б, що константа, переведена з тексту в число, тягне за
        // собою суперечливий рядок — а `IsResolved` вважав би розібраним те,
        // що ним не є.
        value: draft.kind === 'Numeric' ? Number(draft.value) : null,
        unitId: draft.kind === 'Numeric' ? draft.unitId : null,
        textValue: draft.kind === 'Numeric' ? null : draft.textValue,
        validFrom: draft.validFrom === '' ? null : draft.validFrom,
        validTo: draft.validTo === '' ? null : draft.validTo,
        category: draft.category === '' ? null : draft.category,
        substanceEntryId: null,
        source: draft.source === '' ? null : draft.source,
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['methodology-constants', versionId] });
      setEditing(null);
      showDone(t('methodologies.constantSaved'));
    },
    onError: showApiError,
  });

  return (
    <>
      <Group justify="space-between">
        <Text fw={600}>{t('methodologies.constants')}</Text>
        {editable && (
          <Button size="compact-sm" variant="default" onClick={() => setEditing(emptyConstant)}>
            {t('methodologies.addConstant')}
          </Button>
        )}
      </Group>

      <AsyncBoundary<MethodologyConstantDto[]>
        isPending={constants.isPending}
        error={constants.error}
        data={constants.data}
        isEmpty={(list) => list.length === 0}
        emptyTitle={t('methodologies.noConstants')}
        emptyHint={t('methodologies.noConstantsHint')}
        skeleton="table"
        onRetry={() => void constants.refetch()}
      >
        {(list) => (
          <Table striped withTableBorder>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('methodologies.code')}</Table.Th>
                <Table.Th>{t('methodologies.constantKind')}</Table.Th>
                <Table.Th>{t('methodologies.value')}</Table.Th>
                <Table.Th>{t('methodologies.validFrom')}</Table.Th>
                <Table.Th>{t('methodologies.validTo')}</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {list.map((constant) => (
                <Table.Tr key={constant.id}>
                  <Table.Td>{constant.code}</Table.Td>
                  <Table.Td>{constant.kind}</Table.Td>
                  <Table.Td>
                    {/* ⛔ Нерозібране число показується як є і позначається:
                        саме про такі рядки публікація вимагає рішення людини,
                        а мовчазний нуль дав би правдоподібні й неправильні
                        числа. */}
                    {constant.value ?? constant.textValue ?? '—'}
                    {constant.isResolved ? '' : ` · ${t('methodologies.unresolved')}`}
                  </Table.Td>
                  <Table.Td>{constant.validFrom ?? '—'}</Table.Td>
                  <Table.Td>{constant.validTo ?? '—'}</Table.Td>
                  <Table.Td>
                    {editable && (
                      <Button
                        size="compact-xs"
                        variant="subtle"
                        onClick={() =>
                          setEditing({
                            code: constant.code,
                            kind: constant.kind,
                            value: constant.value === null ? '' : String(constant.value),
                            textValue: constant.textValue ?? '',
                            unitId: constant.unitId,
                            validFrom: constant.validFrom ?? '',
                            validTo: constant.validTo ?? '',
                            category: constant.category ?? '',
                            source: constant.source ?? '',
                            isNew: false,
                          })
                        }
                      >
                        {t('methodologies.editFormula')}
                      </Button>
                    )}
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </AsyncBoundary>

      <Modal
        opened={editing !== null}
        onClose={() => setEditing(null)}
        title={t('methodologies.constants')}
      >
        {editing !== null && (
          <Stack gap="sm">
            <TextInput
              label={t('methodologies.code')}
              value={editing.code}
              disabled={!editing.isNew}
              onChange={(event) => setEditing({ ...editing, code: event.currentTarget.value })}
            />

            <Select
              label={t('methodologies.constantKind')}
              description={t('methodologies.constantKindHint')}
              allowDeselect={false}
              value={editing.kind}
              data={[
                { value: 'Numeric', label: t('methodologies.resultNumber') },
                { value: 'Text', label: t('methodologies.resultText') },
                { value: 'CategoryLabel', label: t('methodologies.categoryLabel') },
              ]}
              onChange={(value) =>
                setEditing({
                  ...editing,
                  kind:
                    value === 'Text' ? 'Text' : value === 'CategoryLabel' ? 'CategoryLabel' : 'Numeric',
                })
              }
            />

            {editing.kind === 'Numeric' ? (
              <>
                <TextInput
                  label={t('methodologies.value')}
                  value={editing.value}
                  onChange={(event) => setEditing({ ...editing, value: event.currentTarget.value })}
                />
                <Select
                  label={t('methodologies.outputUnit')}
                  description={t('methodologies.constantUnitHint')}
                  searchable
                  value={editing.unitId === null ? null : String(editing.unitId)}
                  data={(units.data ?? []).map((unit) => ({
                    value: String(unit.id),
                    label: unit.code,
                  }))}
                  onChange={(value) =>
                    setEditing({ ...editing, unitId: value === null ? null : Number(value) })
                  }
                />
              </>
            ) : (
              <TextInput
                label={t('methodologies.constantText')}
                value={editing.textValue}
                onChange={(event) => setEditing({ ...editing, textValue: event.currentTarget.value })}
              />
            )}

            <TextInput
              type="date"
              label={t('methodologies.validFrom')}
              value={editing.validFrom}
              onChange={(event) => setEditing({ ...editing, validFrom: event.currentTarget.value })}
            />

            <TextInput
              type="date"
              label={t('methodologies.validTo')}
              description={t('methodologies.validToHint')}
              value={editing.validTo}
              onChange={(event) => setEditing({ ...editing, validTo: event.currentTarget.value })}
            />

            <TextInput
              label={t('methodologies.category')}
              description={t('methodologies.categoryHint')}
              value={editing.category}
              onChange={(event) => setEditing({ ...editing, category: event.currentTarget.value })}
            />

            <TextInput
              label={t('methodologies.sourceRef')}
              description={t('methodologies.sourceRefHint')}
              value={editing.source}
              onChange={(event) => setEditing({ ...editing, source: event.currentTarget.value })}
            />

            <Button
              disabled={editing.code.trim().length === 0}
              loading={save.isPending}
              onClick={() => save.mutate(editing)}
            >
              {t('methodologies.save')}
            </Button>
          </Stack>
        )}
      </Modal>
    </>
  );
}

/** Правило, яке зараз правлять. */
interface RuleDraft {
  readonly code: string;
  readonly matchJson: string;
  readonly priority: number;
  readonly isActive: boolean;
  readonly isNew: boolean;
}

/**
 * Правила відбору рядків документа (`ФВ-13.3`, `ФВ-13.4`).
 *
 * ⛔ Без жодного правила методологія не зачіпає жодного рядка, і перерахунок
 * завершується успіхом, не порахувавши нічого.
 */
export function MethodologyRulesPanel({
  methodologyId,
  versionId,
  editable,
}: PanelProps): JSX.Element {
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState<RuleDraft | null>(null);

  const rules = useQuery({
    queryKey: ['methodology-rules', versionId],
    queryFn: () => methodologyRules(methodologyId, versionId),
  });

  const save = useMutation({
    mutationFn: (draft: RuleDraft) =>
      saveMethodologyRule(methodologyId, versionId, draft.code, {
        matchJson: draft.matchJson,
        priority: draft.priority,
        isActive: draft.isActive,
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['methodology-rules', versionId] });
      setEditing(null);
      showDone(t('methodologies.ruleSaved'));
    },
    onError: showApiError,
  });

  return (
    <>
      <Group justify="space-between">
        <Text fw={600}>{t('methodologies.rules')}</Text>
        {editable && (
          <Button
            size="compact-sm"
            variant="default"
            onClick={() =>
              setEditing({ code: '', matchJson: '{}', priority: 1, isActive: true, isNew: true })
            }
          >
            {t('methodologies.addRule')}
          </Button>
        )}
      </Group>

      <AsyncBoundary<MethodologyRuleDto[]>
        isPending={rules.isPending}
        error={rules.error}
        data={rules.data}
        isEmpty={(list) => list.length === 0}
        emptyTitle={t('methodologies.noRules')}
        emptyHint={t('methodologies.noRulesHint')}
        skeleton="table"
        onRetry={() => void rules.refetch()}
      >
        {(list) => (
          <Table striped withTableBorder>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('methodologies.code')}</Table.Th>
                <Table.Th>{t('methodologies.priority')}</Table.Th>
                <Table.Th>{t('methodologies.matchJson')}</Table.Th>
                <Table.Th>{t('methodologies.active')}</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {list.map((rule) => (
                <Table.Tr key={rule.id}>
                  <Table.Td>{rule.code}</Table.Td>
                  <Table.Td>{rule.priority}</Table.Td>
                  <Table.Td>
                    <Text size="sm" ff="monospace">
                      {rule.matchJson}
                    </Text>
                  </Table.Td>
                  <Table.Td>{rule.isActive ? '✓' : '—'}</Table.Td>
                  <Table.Td>
                    {editable && (
                      <Button
                        size="compact-xs"
                        variant="subtle"
                        onClick={() =>
                          setEditing({
                            code: rule.code,
                            matchJson: rule.matchJson,
                            priority: rule.priority,
                            isActive: rule.isActive,
                            isNew: false,
                          })
                        }
                      >
                        {t('methodologies.editFormula')}
                      </Button>
                    )}
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </AsyncBoundary>

      <Modal opened={editing !== null} onClose={() => setEditing(null)} title={t('methodologies.rules')}>
        {editing !== null && (
          <Stack gap="sm">
            <TextInput
              label={t('methodologies.code')}
              value={editing.code}
              disabled={!editing.isNew}
              onChange={(event) => setEditing({ ...editing, code: event.currentTarget.value })}
            />

            <Textarea
              label={t('methodologies.matchJson')}
              description={t('methodologies.matchJsonHint')}
              value={editing.matchJson}
              minRows={3}
              autosize
              onChange={(event) => setEditing({ ...editing, matchJson: event.currentTarget.value })}
            />

            <NumberInput
              label={t('methodologies.priority')}
              description={t('methodologies.priorityHint')}
              value={editing.priority}
              onChange={(value) =>
                setEditing({ ...editing, priority: typeof value === 'number' ? value : editing.priority })
              }
            />

            <Checkbox
              label={t('methodologies.active')}
              checked={editing.isActive}
              onChange={(event) => setEditing({ ...editing, isActive: event.currentTarget.checked })}
            />

            <Button
              disabled={editing.code.trim().length === 0 || editing.matchJson.trim().length === 0}
              loading={save.isPending}
              onClick={() => save.mutate(editing)}
            >
              {t('methodologies.save')}
            </Button>
          </Stack>
        )}
      </Modal>
    </>
  );
}

/** Вихід, який зараз правлять. */
interface OutputDraft {
  readonly code: string;
  readonly unitId: number | null;
  readonly ordinal: number;
  readonly isNew: boolean;
}

/**
 * Оголошені виходи версії (`ФВ-16.6`).
 *
 * ⛔ Без жодного виходу модуль рахує всі формули і не записує нічого: цикл
 * запису результату йде саме по виходах.
 */
export function MethodologyOutputsPanel({
  methodologyId,
  versionId,
  editable,
}: PanelProps): JSX.Element {
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState<OutputDraft | null>(null);

  const outputs = useQuery({
    queryKey: ['methodology-outputs', versionId],
    queryFn: () => methodologyOutputs(methodologyId, versionId),
  });

  const units = useQuery({
    queryKey: ['units'],
    queryFn: () => apiFetch<UnitRef[]>('/api/v1/units'),
    staleTime: 60 * 60 * 1000,
  });

  const save = useMutation({
    mutationFn: (draft: OutputDraft) =>
      saveMethodologyOutput(methodologyId, versionId, draft.code, {
        unitId: draft.unitId ?? 0,
        ordinal: draft.ordinal,
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['methodology-outputs', versionId] });
      setEditing(null);
      showDone(t('methodologies.outputSaved'));
    },
    onError: showApiError,
  });

  return (
    <>
      <Group justify="space-between">
        <Text fw={600}>{t('methodologies.outputs')}</Text>
        {editable && (
          <Button
            size="compact-sm"
            variant="default"
            onClick={() => setEditing({ code: '', unitId: null, ordinal: 1, isNew: true })}
          >
            {t('methodologies.addOutput')}
          </Button>
        )}
      </Group>

      <AsyncBoundary<MethodologyOutputDto[]>
        isPending={outputs.isPending}
        error={outputs.error}
        data={outputs.data}
        isEmpty={(list) => list.length === 0}
        emptyTitle={t('methodologies.noOutputs')}
        emptyHint={t('methodologies.noOutputsHint')}
        skeleton="table"
        onRetry={() => void outputs.refetch()}
      >
        {(list) => (
          <Table striped withTableBorder>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('methodologies.code')}</Table.Th>
                <Table.Th>{t('methodologies.outputUnit')}</Table.Th>
                <Table.Th>{t('methodologies.ordinal')}</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {list.map((output) => (
                <Table.Tr key={output.id}>
                  <Table.Td>{output.code}</Table.Td>
                  <Table.Td>
                    {(units.data ?? []).find((u) => u.id === output.unitId)?.code ?? output.unitId}
                  </Table.Td>
                  <Table.Td>{output.ordinal}</Table.Td>
                  <Table.Td>
                    {editable && (
                      <Button
                        size="compact-xs"
                        variant="subtle"
                        onClick={() =>
                          setEditing({
                            code: output.code,
                            unitId: output.unitId,
                            ordinal: output.ordinal,
                            isNew: false,
                          })
                        }
                      >
                        {t('methodologies.editFormula')}
                      </Button>
                    )}
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </AsyncBoundary>

      <Modal
        opened={editing !== null}
        onClose={() => setEditing(null)}
        title={t('methodologies.outputs')}
      >
        {editing !== null && (
          <Stack gap="sm">
            <TextInput
              label={t('methodologies.code')}
              description={t('methodologies.outputCodeHint')}
              value={editing.code}
              disabled={!editing.isNew}
              onChange={(event) => setEditing({ ...editing, code: event.currentTarget.value })}
            />

            <Select
              label={t('methodologies.outputUnit')}
              description={t('methodologies.outputUnitRequiredHint')}
              searchable
              value={editing.unitId === null ? null : String(editing.unitId)}
              data={(units.data ?? []).map((unit) => ({ value: String(unit.id), label: unit.code }))}
              onChange={(value) =>
                setEditing({ ...editing, unitId: value === null ? null : Number(value) })
              }
            />

            <NumberInput
              label={t('methodologies.ordinal')}
              value={editing.ordinal}
              onChange={(value) =>
                setEditing({ ...editing, ordinal: typeof value === 'number' ? value : editing.ordinal })
              }
            />

            <Button
              disabled={editing.code.trim().length === 0 || editing.unitId === null}
              loading={save.isPending}
              onClick={() => save.mutate(editing)}
            >
              {t('methodologies.save')}
            </Button>
          </Stack>
        )}
      </Modal>
    </>
  );
}

/** Тест золотого набору, який зараз правлять. */
interface TestDraft {
  readonly code: string;
  readonly inputJson: string;
  readonly expectedJson: string;
  readonly tolerance: number;
  readonly isNew: boolean;
}

/**
 * Золотий набір версії (`ФВ-13.7`, `ФВ-9.12`).
 *
 * ⛔ Порожній набір — **не** зелений, і версія без нього не публікується.
 * Панель стоїть на тому самому екрані, що й формули, саме тому: інакше кнопка
 * публікації не спрацювала б жодного разу, і причина була б неочевидна.
 */
export function MethodologyTestsPanel({
  methodologyId,
  versionId,
  editable,
}: PanelProps): JSX.Element {
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState<TestDraft | null>(null);

  const tests = useQuery({
    queryKey: ['methodology-tests', versionId],
    queryFn: () => methodologyTestCases(methodologyId, versionId),
  });

  const save = useMutation({
    mutationFn: (draft: TestDraft) =>
      saveMethodologyTestCase(methodologyId, versionId, draft.code, {
        inputJson: draft.inputJson,
        expectedJson: draft.expectedJson,
        tolerance: draft.tolerance,
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['methodology-tests', versionId] });
      setEditing(null);
      showDone(t('methodologies.testSaved'));
    },
    onError: showApiError,
  });

  return (
    <>
      <Group justify="space-between">
        <Text fw={600}>{t('methodologies.tests')}</Text>
        {editable && (
          <Button
            size="compact-sm"
            variant="default"
            onClick={() =>
              setEditing({
                code: '',
                inputJson: '{"periodKey":{"value":0},"arguments":[]}',
                expectedJson: '{}',
                tolerance: 0.0001,
                isNew: true,
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
                <Table.Th>{t('methodologies.code')}</Table.Th>
                <Table.Th>{t('methodologies.expectedJson')}</Table.Th>
                <Table.Th>{t('methodologies.tolerance')}</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {list.map((test) => (
                <Table.Tr key={test.id}>
                  <Table.Td>{test.code}</Table.Td>
                  <Table.Td>
                    <Text size="sm" ff="monospace">
                      {test.expectedJson}
                    </Text>
                  </Table.Td>
                  <Table.Td>{test.tolerance}</Table.Td>
                  <Table.Td>
                    {editable && (
                      <Button
                        size="compact-xs"
                        variant="subtle"
                        onClick={() =>
                          setEditing({
                            code: test.code,
                            inputJson: test.inputJson,
                            expectedJson: test.expectedJson,
                            tolerance: test.tolerance,
                            isNew: false,
                          })
                        }
                      >
                        {t('methodologies.editFormula')}
                      </Button>
                    )}
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </AsyncBoundary>

      <Modal
        opened={editing !== null}
        onClose={() => setEditing(null)}
        title={t('methodologies.tests')}
        size="lg"
      >
        {editing !== null && (
          <Stack gap="sm">
            <TextInput
              label={t('methodologies.code')}
              value={editing.code}
              disabled={!editing.isNew}
              onChange={(event) => setEditing({ ...editing, code: event.currentTarget.value })}
            />

            <Textarea
              label={t('methodologies.inputJson')}
              description={t('methodologies.inputJsonHint')}
              value={editing.inputJson}
              minRows={4}
              autosize
              onChange={(event) => setEditing({ ...editing, inputJson: event.currentTarget.value })}
            />

            <Textarea
              label={t('methodologies.expectedJson')}
              description={t('methodologies.expectedJsonHint')}
              value={editing.expectedJson}
              minRows={2}
              autosize
              onChange={(event) => setEditing({ ...editing, expectedJson: event.currentTarget.value })}
            />

            <NumberInput
              label={t('methodologies.tolerance')}
              description={t('methodologies.toleranceHint')}
              value={editing.tolerance}
              decimalScale={6}
              onChange={(value) =>
                setEditing({
                  ...editing,
                  tolerance: typeof value === 'number' ? value : editing.tolerance,
                })
              }
            />

            <Button
              disabled={editing.code.trim().length === 0}
              loading={save.isPending}
              onClick={() => save.mutate(editing)}
            >
              {t('methodologies.save')}
            </Button>
          </Stack>
        )}
      </Modal>
    </>
  );
}

/** Прив'язка, яку зараз правлять. */
interface BindingDraft {
  readonly columnDefId: number;
  readonly outputCode: string;
  readonly matchJson: string;
  readonly isActive: boolean;
  readonly isNew: boolean;
}

/**
 * Прив'язки методології до колонок документів (`D-69`).
 *
 * ⛔ Головна відсутня ланка: без прив'язки перерахунок документа завершується
 * успіхом і не рахує **нічого** — планувальник бере методології саме звідси, а
 * порожній набір прив'язок помилкою не є.
 *
 * ⚠ Панель на екрані ВЕРСІЇ, хоча прив'язка належить методології: саме тут
 * людина бачить коди виходів, до яких прив'язується, а окремий екран змусив би
 * тримати їх у голові.
 */
export function MethodologyBindingsPanel({
  methodologyId,
  editable,
}: {
  readonly methodologyId: number;
  readonly editable: boolean;
}): JSX.Element {
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState<BindingDraft | null>(null);

  const bindings = useQuery({
    queryKey: ['calculation-bindings', methodologyId],
    queryFn: () => calculationBindings(methodologyId),
  });

  const save = useMutation({
    mutationFn: (draft: BindingDraft) =>
      saveCalculationBinding(methodologyId, draft.columnDefId, draft.outputCode, {
        matchJson: draft.matchJson,
        isActive: draft.isActive,
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['calculation-bindings', methodologyId] });
      setEditing(null);
      showDone(t('methodologies.bindingSaved'));
    },
    onError: showApiError,
  });

  return (
    <>
      <Group justify="space-between">
        <Text fw={600}>{t('methodologies.bindings')}</Text>
        {editable && (
          <Button
            size="compact-sm"
            variant="default"
            onClick={() =>
              setEditing({
                columnDefId: 0,
                outputCode: '',
                matchJson: '{}',
                isActive: true,
                isNew: true,
              })
            }
          >
            {t('methodologies.addBinding')}
          </Button>
        )}
      </Group>

      <AsyncBoundary<CalculationBindingDto[]>
        isPending={bindings.isPending}
        error={bindings.error}
        data={bindings.data}
        isEmpty={(list) => list.length === 0}
        emptyTitle={t('methodologies.noBindings')}
        emptyHint={t('methodologies.noBindingsHint')}
        skeleton="table"
        onRetry={() => void bindings.refetch()}
      >
        {(list) => (
          <Table striped withTableBorder>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('methodologies.tableDefId')}</Table.Th>
                <Table.Th>{t('methodologies.columnDefId')}</Table.Th>
                <Table.Th>{t('methodologies.outputCode')}</Table.Th>
                <Table.Th>{t('methodologies.matchJson')}</Table.Th>
                <Table.Th>{t('methodologies.active')}</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {list.map((binding) => (
                <Table.Tr key={binding.id}>
                  <Table.Td>{binding.tableDefId}</Table.Td>
                  <Table.Td>{binding.columnDefId}</Table.Td>
                  <Table.Td>{binding.outputCode}</Table.Td>
                  <Table.Td>
                    <Text size="sm" ff="monospace">
                      {binding.matchJson}
                    </Text>
                  </Table.Td>
                  <Table.Td>{binding.isActive ? '✓' : '—'}</Table.Td>
                  <Table.Td>
                    {editable && (
                      <Button
                        size="compact-xs"
                        variant="subtle"
                        onClick={() =>
                          setEditing({
                            columnDefId: binding.columnDefId,
                            outputCode: binding.outputCode,
                            matchJson: binding.matchJson,
                            isActive: binding.isActive,
                            isNew: false,
                          })
                        }
                      >
                        {t('methodologies.editFormula')}
                      </Button>
                    )}
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </AsyncBoundary>

      <Modal
        opened={editing !== null}
        onClose={() => setEditing(null)}
        title={t('methodologies.bindings')}
      >
        {editing !== null && (
          <Stack gap="sm">
            <NumberInput
              label={t('methodologies.columnDefId')}
              description={t('methodologies.columnDefIdHint')}
              value={editing.columnDefId}
              disabled={!editing.isNew}
              onChange={(value) =>
                setEditing({
                  ...editing,
                  columnDefId: typeof value === 'number' ? value : editing.columnDefId,
                })
              }
            />

            <TextInput
              label={t('methodologies.outputCode')}
              description={t('methodologies.outputCodeBindingHint')}
              value={editing.outputCode}
              disabled={!editing.isNew}
              onChange={(event) => setEditing({ ...editing, outputCode: event.currentTarget.value })}
            />

            <Textarea
              label={t('methodologies.matchJson')}
              description={t('methodologies.bindingMatchHint')}
              value={editing.matchJson}
              minRows={2}
              autosize
              onChange={(event) => setEditing({ ...editing, matchJson: event.currentTarget.value })}
            />

            <Checkbox
              label={t('methodologies.active')}
              checked={editing.isActive}
              onChange={(event) => setEditing({ ...editing, isActive: event.currentTarget.checked })}
            />

            <Button
              disabled={editing.outputCode.trim().length === 0 || editing.columnDefId <= 0}
              loading={save.isPending}
              onClick={() => save.mutate(editing)}
            >
              {t('methodologies.save')}
            </Button>
          </Stack>
        )}
      </Modal>
    </>
  );
}

/**
 * Режими обчислення версії-чернетки (`ФВ-9.9`, `ФВ-16.11`, `ФВ-9.13`).
 *
 * ⛔ Форма існує тому, що інакше `Strict` увімкнути НЕМОЖЛИВО: конструктор
 * версії ставить `Legacy`, клон переносить режим джерела, а третього шляху не
 * було. Тобто режим, який відрізняє `null` від тихого нуля при діленні на нуль
 * (`ФВ-9.14`), був недосяжним станом системи.
 *
 * ⚠ Обидва перші режими тихо змінюють УСІ числа версії, не змінивши жодної
 * формули — саме тому вони обов'язкові в diff публікації (`D-78`).
 */
export function MethodologyModesForm({
  methodologyId,
  version,
  editable,
}: {
  readonly methodologyId: number;
  readonly version: MethodologyDraftVersionDto;
  readonly editable: boolean;
}): JSX.Element {
  const queryClient = useQueryClient();
  const [numericMode, setNumericMode] = useState(version.numericMode);
  const [calendarMode, setCalendarMode] = useState(version.calendarMode);
  const [traceLevel, setTraceLevel] = useState(version.traceLevel);

  const save = useMutation({
    mutationFn: () =>
      saveMethodologyModes(methodologyId, version.id, { numericMode, calendarMode, traceLevel }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['methodology-versions', methodologyId] });
      showDone(t('methodologies.modesSaved'));
    },
    onError: showApiError,
  });

  return (
    <Stack gap="sm">
      <Text fw={600}>{t('methodologies.modes')}</Text>
      <Text size="sm" c="dimmed">
        {t('methodologies.modesHint')}
      </Text>

      <Group align="end" gap="sm">
        <Select
          label={t('methodologies.numericMode')}
          allowDeselect={false}
          disabled={!editable}
          value={numericMode}
          data={[
            { value: 'Legacy', label: 'Legacy' },
            { value: 'Strict', label: 'Strict' },
          ]}
          onChange={(value) => setNumericMode(value === 'Strict' ? 'Strict' : 'Legacy')}
        />

        <Select
          label={t('methodologies.calendarMode')}
          allowDeselect={false}
          disabled={!editable}
          value={calendarMode}
          data={[
            { value: 'Actual', label: 'Actual' },
            { value: 'Fixed365', label: 'Fixed365' },
            { value: 'Fixed360', label: 'Fixed360' },
          ]}
          onChange={(value) =>
            setCalendarMode(value === 'Fixed365' ? 'Fixed365' : value === 'Fixed360' ? 'Fixed360' : 'Actual')
          }
        />

        <Select
          label={t('methodologies.traceLevel')}
          allowDeselect={false}
          disabled={!editable}
          value={traceLevel}
          data={[
            { value: 'Off', label: 'Off' },
            { value: 'ErrorsOnly', label: 'ErrorsOnly' },
            { value: 'Full', label: 'Full' },
          ]}
          onChange={(value) =>
            setTraceLevel(value === 'Off' ? 'Off' : value === 'Full' ? 'Full' : 'ErrorsOnly')
          }
        />

        {editable && (
          <Button variant="default" loading={save.isPending} onClick={() => save.mutate()}>
            {t('methodologies.saveModes')}
          </Button>
        )}
      </Group>
    </Stack>
  );
}
