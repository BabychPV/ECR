import { useState, type JSX } from 'react';
import {
  Alert,
  Badge,
  Button,
  Checkbox,
  Group,
  Modal,
  Select,
  Stack,
  Table,
  Text,
  Textarea,
  TextInput,
} from '@mantine/core';
import { ResizableModal } from '@/shared/ui/ResizableModal';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type {
  CalculationBindingDto,
  MethodologyDraftVersionDto,
  MethodologyRequiredInputDto,
  RequiredInputSeverity,
} from '@/api/types';
import { queryKeys } from '@/api/queryKeys';
import { t } from '@/shared/i18n';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { Hint } from '@/shared/ui/Hint';
import { showApiError, showDone } from '@/shared/ui/notify';
import {
  calculationBindings,
  deleteMethodologyRequiredInput,
  methodologyDerivedInputs,
  methodologyRequiredInputs,
  saveCalculationBinding,
  saveMethodologyModes,
  saveMethodologyRequiredInput,
} from './api';
import { ColumnDefPicker, savedColumnLabel } from './ColumnDefPicker';
import { MethodologySection } from './MethodologySection';
import { usePendingLoading } from '@/features/common/usePendingLoading';

/**
 * Панелі методології, що лишились після виведення формульного рушія з інтерфейсу (рішення людини 24.11:
 * «має бути тільки наш новий редактор»): обов'язкові вхідні колонки (вкладка сторінки скрипта), прив'язки
 * виходів до колонок і режими обчислення версії. Формули, константи, виходи, правила й золотий набір
 * формульного рушія прибрано — у версії-скрипта все це живе в тексті скрипта.
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

/** Обов'язкова вхідна колонка, яку зараз правлять. */
interface RequiredInputDraft {
  readonly columnDefId: number;
  /** Підпис збереженої колонки для вибору (L9-40); у нового рядка немає. */
  readonly columnLabel?: string;
  readonly severity: RequiredInputSeverity;
  readonly hint: string;
  readonly isNew: boolean;
}

/**
 * Обов'язкові вхідні колонки методології — gate перед збереженням клітинки
 * (директива «обов'язкові вхідні колонки методології»).
 *
 * ⛔ Не те саме, що загальна обов'язковість колонки (`ColumnDef.IsRequired`):
 * ця вимога прив'язана до КОНКРЕТНОЇ методології версії, і та сама колонка
 * може бути обов'язковою для однієї методології таблиці й ні для сусідньої.
 *
 * ⚠ Ключ запису — `ColumnDefId`, а не код: на відміну від правил і виходів,
 * ця сутність не має природного коду.
 */
/** ✎ D-578: підпис рівня вимоги методолога (Block / Warn / Optional). */
function severityLabel(severity: RequiredInputSeverity): string {
  if (severity === 'Optional') return t('methodologies.severityOptional');
  return severity === 'Warn' ? t('methodologies.severityWarn') : t('methodologies.severityBlock');
}

/**
 * ✎ D-578 (рішення людини 26.11): поля, які читає скрипт версії, — обов'язкові для подання автоматично (лише читання).
 * Послабити вимогу може лише методолог — заведенням поля в перелік вище як Warn чи Optional; рядок показує, який виняток
 * діє. Автоматичного переліку немає (скрипт не скомпілювався чи поля не записано) — попередження; формульна версія —
 * секції немає.
 */
function DerivedInputsTable({ methodologyId, versionId }: { methodologyId: number; versionId: number }): JSX.Element | null {
  const derived = useQuery({
    queryKey: queryKeys.methodologies.derivedInputs(versionId),
    queryFn: () => methodologyDerivedInputs(methodologyId, versionId),
  });

  if (derived.data === undefined || derived.data.status === 'notScript') return null;

  return (
    <Stack gap="xs" mt="md" data-testid="methodology-derived-inputs">
      <Text fw={600} size="sm">{t('methodologies.derivedInputs')}</Text>
      {derived.data.status !== 'available' ? (
        <Alert color="statusWarning" variant="light" data-testid="methodology-derived-unavailable">
          {t('methodologies.derivedUnavailable')}
        </Alert>
      ) : (
        <>
          <Text size="xs" c="dimmed">{t('methodologies.derivedInputsHint')}</Text>
          <Table withTableBorder>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('methodologies.derivedField')}</Table.Th>
                <Table.Th>{t('methodologies.derivedRequirement')}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {derived.data.fields.map((field) => (
                <Table.Tr key={field.fieldCode}>
                  <Table.Td>{field.fieldCode}</Table.Td>
                  <Table.Td>
                    {!field.inScope
                      ? t('methodologies.derivedOtherMonths')
                      : field.exceptionSeverity === undefined || field.exceptionSeverity === null
                        ? t('methodologies.derivedRequired')
                        : severityLabel(field.exceptionSeverity)}
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </>
      )}
    </Stack>
  );
}

export function MethodologyRequiredInputsPanel({
  methodologyId,
  versionId,
  editable,
}: PanelProps): JSX.Element {
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState<RequiredInputDraft | null>(null);
  // ✎ P-1124-1: прибрати вимогу — лише після підтвердження (як видалення чернетки версії).
  const [removing, setRemoving] = useState<MethodologyRequiredInputDto | null>(null);

  const requiredInputs = useQuery({
    queryKey: queryKeys.methodologies.requiredInputs(versionId),
    queryFn: () => methodologyRequiredInputs(methodologyId, versionId),
  });

  const save = useMutation({
    mutationFn: (draft: RequiredInputDraft) =>
      saveMethodologyRequiredInput(methodologyId, versionId, draft.columnDefId, {
        severity: draft.severity,
        hintL10n: draft.hint.trim().length === 0 ? null : { en: draft.hint },
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({
        queryKey: queryKeys.methodologies.requiredInputs(versionId),
      });
      setEditing(null);
      showDone(t('methodologies.requiredInputSaved'));
    },
    onError: showApiError,
  });

  // ⚠ `ФВ-14.26`: спінер на кнопці — лише після 100 мс дії, не з першого кадру.
  const saveLoading = usePendingLoading(save.isPending);

  const remove = useMutation({
    mutationFn: (target: MethodologyRequiredInputDto) =>
      deleteMethodologyRequiredInput(methodologyId, versionId, target.columnDefId),
    onSuccess: async () => {
      await queryClient.invalidateQueries({
        queryKey: queryKeys.methodologies.requiredInputs(versionId),
      });
      setRemoving(null);
      showDone(t('methodologies.requiredInputRemoved'));
    },
    onError: showApiError,
  });
  const removeLoading = usePendingLoading(remove.isPending);

  return (
    <MethodologySection
      sectionId="requiredInputs"
      title={t('methodologies.requiredInputs')}
      actions={editable && (
          <Button
            size="xs"
            variant="default"
            onClick={() =>
              setEditing({ columnDefId: 0, severity: 'Block', hint: '', isNew: true })
            }
          >
            {t('methodologies.addRequiredInput')}
          </Button>
        )}
    >

      <AsyncBoundary<MethodologyRequiredInputDto[]>
        isPending={requiredInputs.isPending}
        error={requiredInputs.error}
        data={requiredInputs.data}
        isEmpty={(list) => list.length === 0}
        emptyTitle={t('methodologies.noRequiredInputs')}
        emptyHint={t('methodologies.noRequiredInputsHint')}
        skeleton="table"
        onRetry={() => void requiredInputs.refetch()}
      >
        {(list) => (
          <Table striped withTableBorder>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('methodologies.columnDefId')}</Table.Th>
                <Table.Th>{t('methodologies.severity')}</Table.Th>
                <Table.Th>{t('methodologies.hint')}</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {list.map((requiredInput) => (
                <Table.Tr key={requiredInput.id}>
                  <Table.Td>
                    <Group gap="xs" wrap="nowrap">
                      {requiredInput.columnCode ?? requiredInput.columnDefId}
                      {/* ⛔ UI-аудит, lane 5 (`Q-337`): деактивована прив'язка
                          лишала вимогу без ЖОДНОГО натяку, що вона зависла —
                          рядок і далі показував `Column: X, Severity: Block`
                          так, наче все гаразд, і далі блокував збереження
                          даних для колонки, яку методологія вже не пише. */}
                      {/* ⚠ `Hint` із `focusable`, а не `Tooltip`: бейдж не в
                          порядку табуляції, і `Tooltip` показував пояснення
                          лише миші — з клавіатури його не було видно ніколи. */}
                      {!requiredInput.hasActiveBinding && (
                        <Hint label={t('methodologies.requiredInputUnattachedHint')} focusable>
                          <Badge size="xs" color="statusWarning" variant="outline">
                            {t('methodologies.requiredInputUnattached')}
                          </Badge>
                        </Hint>
                      )}
                    </Group>
                  </Table.Td>
                  <Table.Td>
                    {severityLabel(requiredInput.severity)}
                  </Table.Td>
                  <Table.Td>
                    <Text size="sm" c="dimmed">
                      {requiredInput.hintL10n?.['en'] ?? '—'}
                    </Text>
                  </Table.Td>
                  <Table.Td>
                    {editable && (
                      <Button
                        size="xs"
                        variant="subtle"
                        onClick={() =>
                          setEditing({
                            columnDefId: requiredInput.columnDefId,
                            columnLabel: savedColumnLabel(requiredInput.columnDefId, requiredInput.columnCode),
                            severity: requiredInput.severity,
                            hint: requiredInput.hintL10n?.['en'] ?? '',
                            isNew: false,
                          })
                        }
                      >
                        {t('methodologies.editFormula')}
                      </Button>
                    )}
                    {editable && (
                      <Button
                        size="xs"
                        variant="subtle"
                        color="statusError"
                        onClick={() => setRemoving(requiredInput)}
                      >
                        {t('methodologies.requiredInputRemove')}
                      </Button>
                    )}
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </AsyncBoundary>

      <DerivedInputsTable methodologyId={methodologyId} versionId={versionId} />

      {/* ⛔ Закриття без кнопки (хрестик, Esc) — скасування: запиту немає. */}
      <Modal
        opened={removing !== null}
        onClose={() => setRemoving(null)}
        title={t('methodologies.requiredInputRemoveTitle')}
      >
        <Text size="sm" mb="md">
          {t('methodologies.requiredInputRemoveText', {
            column: removing === null ? '' : String(removing.columnCode ?? removing.columnDefId),
          })}
        </Text>
        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={() => setRemoving(null)}>
            {t('common.cancel')}
          </Button>
          <Button
            color="statusError"
            loading={removeLoading}
            onClick={() => {
              // ⛔ B149: кнопка закритого діалогу (~200 мс у DOM) не має слати запит.
              if (removing === null || remove.isPending) return;
              remove.mutate(removing);
            }}
          >
            {t('methodologies.requiredInputRemove')}
          </Button>
        </Group>
      </Modal>

      <ResizableModal
        sizeKey="methodology.requiredInput"
        opened={editing !== null}
        onClose={() => setEditing(null)}
        title={t('methodologies.requiredInputs')}
      >
        {editing !== null && (
          <Stack gap="sm">
            {/* ⛔ Директива "пошук колонки за назвою замість голого
                ColumnDefId": адміністратор більше не має пам'ятати
                внутрішній ідентифікатор — вибір за назвою чи кодом,
                той самий прийом, що вибір довідника в `ColumnEditor.tsx`. */}
            {/* ⛔ Відмова пошуку робила перелік порожнім, і пошук за назвою
                відповідав «нічого не знайдено» на БУДЬ-ЯКИЙ запит. Людина
                читає це як факт («такої колонки в шаблонах немає») і йде
                перевіряти, чи опублікована версія шаблону, — замість
                повторити запит. */}
            <ColumnDefPicker
              label={t('methodologies.columnDefId')}
              description={t('methodologies.requiredInputColumnHint')}
              disabled={!editing.isNew}
              value={editing.columnDefId}
              currentLabel={editing.columnLabel}
              onChange={(columnDefId) => setEditing({ ...editing, columnDefId })}
            />

            <Select
              label={t('methodologies.severity')}
              description={t('methodologies.severityHint')}
              data={[
                { value: 'Block', label: t('methodologies.severityBlock') },
                { value: 'Warn', label: t('methodologies.severityWarn') },
                { value: 'Optional', label: t('methodologies.severityOptional') },
              ]}
              value={editing.severity}
              allowDeselect={false}
              onChange={(value) =>
                setEditing({
                  ...editing,
                  severity: value === 'Warn' || value === 'Optional' ? value : 'Block',
                })
              }
            />

            <Textarea
              label={t('methodologies.hint')}
              description={t('methodologies.hintHint')}
              value={editing.hint}
              minRows={2}
              autosize
              onChange={(event) => setEditing({ ...editing, hint: event.currentTarget.value })}
            />

            <Button
              disabled={editing.columnDefId <= 0}
              loading={saveLoading}
              onClick={() => {
                if (save.isPending) return;
                save.mutate(editing);
              }}
            >
              {t('methodologies.save')}
            </Button>
          </Stack>
        )}
      </ResizableModal>
    </MethodologySection>
  );
}

/** Прив'язка, яку зараз правлять. */
interface BindingDraft {
  readonly columnDefId: number;
  /** Підпис збереженої колонки для вибору (L9-40); у нового рядка немає. */
  readonly columnLabel?: string;
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
    queryKey: queryKeys.methodologies.bindings(methodologyId),
    queryFn: () => calculationBindings(methodologyId),
  });

  const save = useMutation({
    mutationFn: (draft: BindingDraft) =>
      saveCalculationBinding(methodologyId, draft.columnDefId, draft.outputCode, {
        matchJson: draft.matchJson,
        isActive: draft.isActive,
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.methodologies.bindings(methodologyId) });
      setEditing(null);
      showDone(t('methodologies.bindingSaved'));
    },
    onError: showApiError,
  });

  // ⚠ `ФВ-14.26`: спінер на кнопці — лише після 100 мс дії, не з першого кадру.
  const saveLoading = usePendingLoading(save.isPending);

  return (
    <MethodologySection
      sectionId="bindings"
      title={t('methodologies.bindings')}
      actions={editable && (
          <Button
            size="xs"
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
    >

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
                  {/* ⚠ F-21: таблиця й колонка — кодами, а не «193 / 6017»;
                      ідентифікатор лишається лише як запасний варіант. */}
                  <Table.Td>{binding.tableCode ?? binding.tableDefId}</Table.Td>
                  <Table.Td>{binding.columnCode ?? binding.columnDefId}</Table.Td>
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
                        size="xs"
                        variant="subtle"
                        onClick={() =>
                          setEditing({
                            columnDefId: binding.columnDefId,
                            columnLabel: savedColumnLabel(binding.columnDefId, binding.columnCode, binding.tableCode),
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

      <ResizableModal
        sizeKey="methodology.binding"
        opened={editing !== null}
        onClose={() => setEditing(null)}
        title={t('methodologies.bindings')}
      >
        {editing !== null && (
          <Stack gap="sm">
            {/* ⛔ Директива "пошук колонки за назвою замість голого
                ColumnDefId": той самий прийом, що вибір довідника в
                `ColumnEditor.tsx` — пошук наскрізь по всіх версіях, бо
                прив'язка не обмежена ОДНІЄЮ таблицею. */}
            {/* ⛔ Той самий запит, та сама ціна, що в обов'язкових входах — і
                саме тому обидва споживачі полагоджені разом: полагодити один
                означало б лишити другий екран із тією самою мовчазною
                порожнечею. */}
            <ColumnDefPicker
              label={t('methodologies.columnDefId')}
              description={t('methodologies.columnDefIdHint')}
              disabled={!editing.isNew}
              value={editing.columnDefId}
              currentLabel={editing.columnLabel}
              onChange={(columnDefId) => setEditing({ ...editing, columnDefId })}
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
              loading={saveLoading}
              onClick={() => {
                if (save.isPending) return;
                save.mutate(editing);
              }}
            >
              {t('methodologies.save')}
            </Button>
          </Stack>
        )}
      </ResizableModal>
    </MethodologySection>
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
  const [loadedFor, setLoadedFor] = useState(version.id);

  /*
   * ⛔ Аудит 2026-09-16 §10.5 (High, тиха псування даних): цього блоку не було,
   * і локальний стан наповнювався РІВНО ОДИН раз — початковим значенням
   * `useState` при монтуванні. Компонент рендериться з пропу
   * `version = selected` того самого списку (`MethodologyVersionsPage.tsx`), а
   * перемикання версії в списку МІНЯЄ ПРОП, не перемонтовуючи компонент: у
   * `Select`-ах лишалися режими попередньої версії, візуально не відрізнити
   * від справжніх. «Зберегти режими» надсилало їх у версію B за правильною
   * адресою — тобто тихо перезаписувало режими B значеннями A. Ціна названа
   * власними коментарями цього файлу: обидва перші режими «тихо змінюють УСІ
   * числа версії, не змінивши жодної формули» (`ФВ-9.9`, `ФВ-16.11`).
   *
   * ⚠ Синхронізація ПРИ РЕНДЕРІ, а не в `useEffect`, — той самий взірець
   * `loadedFor`, що вже стоїть у `PresentationEditor.tsx` і
   * `RegistryEntryEditor.tsx`: ефект дав би зайвий рендер зі значеннями ЧУЖОЇ
   * версії між кліком і синхронізацією, тобто той самий дефект, лише на один
   * кадр.
   *
   * ⚠ Умова — саме `version.id`, а не порівняння самих режимів: інакше
   * синхронізація затирала б власний вибір адміна (він щойно змінив Select, а
   * проп лишився старим) на першому ж перерендері батька.
   */
  if (loadedFor !== version.id) {
    setLoadedFor(version.id);
    setNumericMode(version.numericMode);
    setCalendarMode(version.calendarMode);
    setTraceLevel(version.traceLevel);
  }

  const save = useMutation({
    mutationFn: () =>
      saveMethodologyModes(methodologyId, version.id, { numericMode, calendarMode, traceLevel }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({
        queryKey: queryKeys.methodologies.versionsOf(methodologyId),
      });
      showDone(t('methodologies.modesSaved'));
    },
    onError: showApiError,
  });

  // ⚠ `ФВ-14.26`: спінер на кнопці — лише після 100 мс дії, не з першого кадру.
  const saveLoading = usePendingLoading(save.isPending);

  return (
    <MethodologySection sectionId="modes" title={t('methodologies.modes')}>
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
          <Button
            variant="default"
            loading={saveLoading}
            onClick={() => {
              if (save.isPending) return;
              save.mutate();
            }}
          >
            {t('methodologies.saveModes')}
          </Button>
        )}
      </Group>
    </MethodologySection>
  );
}
