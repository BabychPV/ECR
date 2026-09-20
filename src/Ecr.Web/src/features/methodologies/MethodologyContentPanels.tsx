import { useState, type JSX } from 'react';
import {
  Badge,
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
  Tooltip,
} from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type {
  CalculationBindingDto,
  ColumnDefSearchResultDto,
  MethodologyConstantDto,
  MethodologyDraftVersionDto,
  MethodologyOutputDto,
  MethodologyRequiredInputDto,
  MethodologyRuleDto,
  MethodologyTestCaseDto,
  RequiredInputSeverity,
  UnitRef,
} from '@/api/types';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { Timestamp } from '@/shared/ui/Timestamp';
import { showApiError, showDone } from '@/shared/ui/notify';
import {
  calculationBindings,
  methodologyConstants,
  methodologyOutputs,
  methodologyRequiredInputs,
  methodologyRules,
  methodologyTestCases,
  saveCalculationBinding,
  saveMethodologyConstant,
  saveMethodologyModes,
  saveMethodologyOutput,
  saveMethodologyRequiredInput,
  saveMethodologyRule,
  saveMethodologyTestCase,
} from './api';

/**
 * Список колонок для пошуку за назвою (директива "пошук колонки за назвою
 * замість голого ColumnDefId") — той самий прийом, що вибір довідника в
 * `ColumnEditor.tsx` (`Select searchable`, лейбл `Назва (КОД)`, наповнений
 * ОДНИМ запитом без живого пошуку по мережі на кожне натискання).
 *
 * ⚠ Пошук наскрізний по всіх версіях шаблонів одразу (`limit=200`): прив'язка
 * методології не обмежена ОДНІЄЮ таблицею — `TableDefId` виводиться із самої
 * колонки (`SaveCalculationBindingHandler`), тому й колонку для вибору
 * потрібно шукати серед усіх, а не лише в межах контексту цього екрана.
 */
interface ColumnDefChoices {
  /**
   * Пункти `Select`.
   *
   * ⛔ Порожньо тут означає РІВНО ОДНЕ — колонок справді немає. Доки запит їде
   * або відмовив, викликач не малює `Select` узагалі (`error`/`isPending`
   * нижче), тож порожній перелік більше не є трьома різними станами одразу.
   */
  readonly options: { value: string; label: string }[];

  /** Відмова читання; `null` — запит удався. */
  readonly error: unknown;

  /** Чи перелік іще їде. */
  readonly isPending: boolean;

  /** Повторити читання. */
  readonly refetch: () => void;
}

function useColumnDefOptions(): ColumnDefChoices {
  const columns = useQuery({
    queryKey: ['column-defs-search'],
    queryFn: () => apiFetch<ColumnDefSearchResultDto[]>('/api/v1/column-defs/search?limit=200'),
    staleTime: 60 * 1000,
  });

  return {
    options: (columns.data ?? []).map((column) => ({
      value: String(column.id),
      label: `${localized(column.headerL10n) || column.code} (${column.code}) · ${column.sheetCode}/${column.tableCode}`,
    })),
    error: columns.error,
    isPending: columns.isPending,
    refetch: () => {
      void columns.refetch();
    },
  };
}

/**
 * Вибір із допоміжного довідника, який МОЖЕ не приїхати (директива №15, §0,
 * `L10`).
 *
 * ⛔ Раніше кожен такий `Select` наповнювався через `?? []`, і порожній перелік
 * означав три різні речі одразу: «довідник порожній», «ще їде», «сервер
 * відмовив». Людина читає найгірше з трьох — ПЕРШЕ, бо саме воно схоже на
 * факт: «колонки такої немає», «одиниць у системі немає». І діє за цим фактом:
 * іде перевіряти, чи опублікована версія шаблону, або зберігає константу без
 * одиниці, або вирішує, що виходи тут завести неможливо.
 *
 * ⚠ Порядок той самий, що в `AsyncBoundary`: помилка ПЕРШОЮ, бо невдалий запит
 * теж лишає дані порожніми. Різниця лише в тому, що сама `AsyncBoundary` тут
 * не годиться — вона малює власний `<Title order={4}>` порожнього стану, а
 * всередині модалки це рве `heading-order` і валить гейт `a11y`.
 *
 * ⚠ «Ще їде» — НЕДОСТУПНИЙ контрол, а не порожній: людина бачить поле на його
 * місці (розмітка не стрибає), але не може обрати з переліку, якого ще немає.
 */
function ChoiceField({
  error,
  isPending,
  onRetry,
  children,
}: {
  readonly error: unknown;
  readonly isPending: boolean;
  readonly onRetry: () => void;
  readonly children: (disabled: boolean) => JSX.Element;
}): JSX.Element {
  if (error !== null && error !== undefined) {
    return <ErrorAlert error={error} onRetry={onRetry} />;
  }

  return children(isPending);
}

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

/**
 * Що стоїть у межі вікна дії, коли межі НЕМАЄ.
 *
 * ⛔ Не тире. Тире — дефолт `Timestamp` і читається як «значення немає», а
 * контракт каже інше й каже це прямо: `validFrom: null` — «від початку»,
 * `validTo: null` — «без межі» (`MethodologyConstantDto`). Константа з
 * порожнім `validTo` не «не має дати кінця» — вона **чинна й далі**, і саме
 * це відрізняє її від константи, у якої строк вичерпався. Тире тут збрехало б
 * рівно в той бік, у який помилитися найдорожче: людина шукає, чому коефіцієнт
 * не підставляється, а комірка каже «тут порожньо».
 *
 * ⚠ Символ, а не слово з каталогу, навмисно: напис («безстроково») — це новий
 * ключ `ui-strings` у трьох мовах і рядок сіду, тобто зміна поза межами цієї
 * підзадачі. Три крапки читаються однаково в усіх трьох мовах каталогу і з
 * обох боків вікна: `… — 2025-01-01` і `2024-01-01 — …`.
 */
const Unbounded = '…';

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
    queryKey: queryKeys.methodologies.constants(versionId),
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
      await queryClient.invalidateQueries({ queryKey: queryKeys.methodologies.constants(versionId) });
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
                  {/* ⚠ `dateOnly` в обох колонках. Вікно дії константи
                      порівнюється з ДНЕМ періоду («чи чинний цей коефіцієнт у
                      березні 2026»), і `validTo` — перший НЕчинний день
                      (виключна межа, `saveMethodologyConstant`). Година тут не
                      лише зайва — вона зробила б виключну межу схожою на
                      момент, тобто підказувала б, що опівдні 2025-01-01
                      константа ще діє. */}
                  <Table.Td>
                    <Timestamp value={constant.validFrom} dateOnly fallback={Unbounded} />
                  </Table.Td>
                  <Table.Td>
                    <Timestamp value={constant.validTo} dateOnly fallback={Unbounded} />
                  </Table.Td>
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
                {/* ⛔ Тут ціна порожнечі — ТИХО БЕЗРОЗМІРНА КОНСТАНТА. Одиниця
                    в константі необов'язкова (`unitId: number | null`), тож
                    сервер приймає запис без неї й нічого не каже. Порожній
                    перелік читався як «одиниць у системі немає», людина
                    зберігала коефіцієнт без одиниці — і відмова ЧИТАННЯ
                    довідника перетворювалася на неправильні дані. */}
                <ChoiceField
                  error={units.error}
                  isPending={units.isPending}
                  onRetry={() => void units.refetch()}
                >
                  {(disabled) => (
                    <Select
                      label={t('methodologies.outputUnit')}
                      description={t('methodologies.constantUnitHint')}
                      searchable
                      disabled={disabled}
                      value={editing.unitId === null ? null : String(editing.unitId)}
                      data={(units.data ?? []).map((unit) => ({
                        value: String(unit.id),
                        label: unit.code,
                      }))}
                      onChange={(value) =>
                        setEditing({ ...editing, unitId: value === null ? null : Number(value) })
                      }
                    />
                  )}
                </ChoiceField>
              </>
            ) : (
              <TextInput
                label={t('methodologies.constantText')}
                value={editing.textValue}
                onChange={(event) => setEditing({ ...editing, textValue: event.currentTarget.value })}
              />
            )}

            <TextInput
              // eslint-disable-next-line no-restricted-syntax -- D15-09, борг №3/8: перехід на DateInput змінює тип значення (string → Date), тому окремим PR; список боргу сторожить lintRules.test.ts
              type="date"
              label={t('methodologies.validFrom')}
              value={editing.validFrom}
              onChange={(event) => setEditing({ ...editing, validFrom: event.currentTarget.value })}
            />

            <TextInput
              // eslint-disable-next-line no-restricted-syntax -- D15-09, борг №4/8: див. коментар вище
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

/**
 * Чи предикат — catch-all («порожній об'єкт відповідає всій таблиці»).
 *
 * ⛔ UI-аудит, lane 5: діалог сам стверджує інваріант («An empty object
 * matches the whole table - which is why such a rule must have the lowest
 * priority»), але ніде його не перевіряв. Синтаксично некоректний JSON тут
 * НЕ catch-all — це просто ще не готовий чернетковий текст, і `false` за
 * замовчуванням не піднімає хибне попередження на кожному натисканні
 * клавіші.
 */
function isCatchAllMatchJson(matchJson: string): boolean {
  try {
    const parsed: unknown = JSON.parse(matchJson);
    return (
      typeof parsed === 'object' &&
      parsed !== null &&
      !Array.isArray(parsed) &&
      Object.keys(parsed).length === 0
    );
  } catch {
    return false;
  }
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
    queryKey: queryKeys.methodologies.rules(versionId),
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
      await queryClient.invalidateQueries({ queryKey: queryKeys.methodologies.rules(versionId) });
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
        {editing !== null && (() => {
          // ⛔ UI-аудит, lane 5: діалог сам пояснює інваріант («An empty
          // object matches the whole table - which is why such a rule must
          // have the lowest priority» / «The lower the number, the higher
          // the priority»), але зберігав будь-яке порушення мовчки: ні
          // підтвердження, ні попередження, ні позначки в таблиці. Реальний
          // редактор міг згодом додати друге правило й ніколи не помітити,
          // що воно вже недосяжне, — доти, доки хтось не почне з'ясовувати,
          // чому розрахунок не бачить рядків, які має бачити.
          // ⛔ А тепер те, що робило саме ці два попередження гіршими за їх
          // відсутність. Кнопка «Додати правило» стоїть ПОЗА `AsyncBoundary`,
          // тож при відмові `GET …/rules` таблиця показує банер, а діалог усе
          // одно відкривається — з `otherRules`, зібраним через `?? []`, тобто
          // ПОРОЖНІМ. Обидві перевірки нижче мовчать (`maxOtherPriority === null`,
          // `blockingCatchAll === undefined`), і catch-all із пріоритетом 1
          // зберігається без жодного слова, перекривши всі точніші правила.
          //
          // ⛔ Тобто рівно тоді, коли клієнт НЕ ЗНАЄ, які правила вже є, він
          // повідомляє, що конфлікту немає. Запобіжник, який деградує в бік
          // ДОЗВОЛУ, — гірший за відсутній: він ще й заспокоює.
          //
          // ⚠ Ховати «Додати правило» не треба: завести правило законно й при
          // недоступному переліку. Недоступним стає лише ЗБЕРЕЖЕННЯ — і поруч
          // стоїть причина з кодом відмови, а не мертва кнопка (той самий
          // висновок, що в `pages/admin/PeriodsPage.tsx` про архівацію).
          //
          // ⚠ `data === undefined` тут не зайве поруч із `error`: доки запит у
          // дорозі, перелік так само невідомий, і висновок «конфлікту немає»
          // так само не має підстав.
          const rulesUnknown = rules.error !== null || rules.data === undefined;

          const otherRules = (rules.data ?? []).filter((rule) => editing.isNew || rule.code !== editing.code);
          const editingIsCatchAll = isCatchAllMatchJson(editing.matchJson);
          const maxOtherPriority =
            otherRules.length === 0 ? null : Math.max(...otherRules.map((rule) => rule.priority));

          // Catch-all повинен мати НАЙБІЛЬШЕ число (перевіряється останнім).
          const catchAllNotLowest =
            editingIsCatchAll && maxOtherPriority !== null && editing.priority <= maxOtherPriority;

          // Існуючий catch-all з МЕНШИМ числом (вищим пріоритетом) заявляє
          // на себе кожен рядок раніше, ніж черга дійде до цього правила.
          const blockingCatchAll = otherRules.find(
            (rule) => isCatchAllMatchJson(rule.matchJson) && rule.priority < editing.priority,
          );

          return (
          <Stack gap="sm">
            {/* ⚠ Помилка ПЕРШОЮ — той самий порядок, що в `AsyncBoundary` і в
                `ChoiceField` вище. Сама `AsyncBoundary` тут не годиться: її
                `<Title order={4}>` усередині модалки рве `heading-order` і
                валить гейти `a11y (dark)`/`a11y (light)`.

                ⚠ Доки запит у дорозі, `rules.error === null`, і банера немає
                зовсім (`ErrorAlert` повертає `null`) — недоступна кнопка там
                самоусувається за секунду, як і `disabled` у `ChoiceField`. */}
            <ErrorAlert error={rules.error} onRetry={() => void rules.refetch()} />

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

            {catchAllNotLowest && (
              <Stack gap="xs">
                <Text size="sm" fw={600} c="statusWarning">
                  {t('methodologies.catchAllNotLowestTitle')}
                </Text>
                <Text size="sm" c="dimmed">
                  {t('methodologies.catchAllNotLowestWarning')}
                </Text>
              </Stack>
            )}

            {blockingCatchAll !== undefined && (
              <Stack gap="xs">
                <Text size="sm" fw={600} c="statusWarning">
                  {t('methodologies.shadowedByCatchAllTitle')}
                </Text>
                <Text size="sm" c="dimmed">
                  {t('methodologies.shadowedByCatchAllWarning', { code: blockingCatchAll.code })}
                </Text>
              </Stack>
            )}

            <Checkbox
              label={t('methodologies.active')}
              checked={editing.isActive}
              onChange={(event) => setEditing({ ...editing, isActive: event.currentTarget.checked })}
            />

            {/* ⛔ `rulesUnknown` — не зручність, а межа: без переліку правил
                обидва попередження вище нічого не перевіряють, тож зберегти
                означало б зберегти НАОСЛІП. Причина стоїть банером угорі. */}
            <Button
              disabled={
                rulesUnknown ||
                editing.code.trim().length === 0 ||
                editing.matchJson.trim().length === 0
              }
              loading={save.isPending}
              onClick={() => save.mutate(editing)}
            >
              {t('methodologies.save')}
            </Button>
          </Stack>
          );
        })()}
      </Modal>
    </>
  );
}

/** Обов'язкова вхідна колонка, яку зараз правлять. */
interface RequiredInputDraft {
  readonly columnDefId: number;
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
export function MethodologyRequiredInputsPanel({
  methodologyId,
  versionId,
  editable,
}: PanelProps): JSX.Element {
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState<RequiredInputDraft | null>(null);
  const columns = useColumnDefOptions();

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

  return (
    <>
      <Group justify="space-between">
        <Text fw={600}>{t('methodologies.requiredInputs')}</Text>
        {editable && (
          <Button
            size="compact-sm"
            variant="default"
            onClick={() =>
              setEditing({ columnDefId: 0, severity: 'Block', hint: '', isNew: true })
            }
          >
            {t('methodologies.addRequiredInput')}
          </Button>
        )}
      </Group>

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
                      {requiredInput.columnDefId}
                      {/* ⛔ UI-аудит, lane 5 (`Q-337`): деактивована прив'язка
                          лишала вимогу без ЖОДНОГО натяку, що вона зависла —
                          рядок і далі показував `Column: X, Severity: Block`
                          так, наче все гаразд, і далі блокував збереження
                          даних для колонки, яку методологія вже не пише. */}
                      {!requiredInput.hasActiveBinding && (
                        <Tooltip label={t('methodologies.requiredInputUnattachedHint')} multiline w={280}>
                          <Badge size="xs" color="statusWarning" variant="outline">
                            {t('methodologies.requiredInputUnattached')}
                          </Badge>
                        </Tooltip>
                      )}
                    </Group>
                  </Table.Td>
                  <Table.Td>
                    {requiredInput.severity === 'Block'
                      ? t('methodologies.severityBlock')
                      : t('methodologies.severityWarn')}
                  </Table.Td>
                  <Table.Td>
                    <Text size="sm" c="dimmed">
                      {requiredInput.hintL10n?.['en'] ?? '—'}
                    </Text>
                  </Table.Td>
                  <Table.Td>
                    {editable && (
                      <Button
                        size="compact-xs"
                        variant="subtle"
                        onClick={() =>
                          setEditing({
                            columnDefId: requiredInput.columnDefId,
                            severity: requiredInput.severity,
                            hint: requiredInput.hintL10n?.['en'] ?? '',
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
            <ChoiceField
              error={columns.error}
              isPending={columns.isPending}
              onRetry={columns.refetch}
            >
              {(disabled) => (
                <Select
                  label={t('methodologies.columnDefId')}
                  description={t('methodologies.requiredInputColumnHint')}
                  searchable
                  disabled={!editing.isNew || disabled}
                  value={editing.columnDefId === 0 ? null : String(editing.columnDefId)}
                  data={columns.options}
                  onChange={(value) =>
                    setEditing({
                      ...editing,
                      columnDefId: value === null ? 0 : Number(value),
                    })
                  }
                />
              )}
            </ChoiceField>

            <Select
              label={t('methodologies.severity')}
              description={t('methodologies.severityHint')}
              data={[
                { value: 'Block', label: t('methodologies.severityBlock') },
                { value: 'Warn', label: t('methodologies.severityWarn') },
              ]}
              value={editing.severity}
              allowDeselect={false}
              onChange={(value) =>
                setEditing({
                  ...editing,
                  severity: value === 'Warn' ? 'Warn' : 'Block',
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
    queryKey: queryKeys.methodologies.outputs(versionId),
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
      await queryClient.invalidateQueries({ queryKey: queryKeys.methodologies.outputs(versionId) });
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
                    {/* ✎ Це `?? []` лишено НАВМИСНО, і межа тут чесна: коли
                        довідник не приїхав, комірка показує сам `unitId` —
                        тобто каже щось, а не мовчить. Людина бачить число
                        замість коду й розуміє, що підпис не розв'язався;
                        хибного факту («одиниці немає») тут не виникає, бо
                        одиниця виходу обов'язкова й НЕпорожня за побудовою.
                        Банер відмови в кожному рядку таблиці коштував би
                        дорожче за користь. */}
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

            {/* ⛔ Тут одиниця ОБОВ'ЯЗКОВА (кнопка нижче недоступна, доки
                `unitId === null`), тож порожній перелік не псував даних — він
                мовчав. Людина бачила порожній вибір і мертву кнопку
                «Зберегти» й читала з цього, що виходи тут завести неможливо;
                причини — відмови читання довідника — не бачив ніхто. */}
            <ChoiceField
              error={units.error}
              isPending={units.isPending}
              onRetry={() => void units.refetch()}
            >
              {(disabled) => (
                <Select
                  label={t('methodologies.outputUnit')}
                  description={t('methodologies.outputUnitRequiredHint')}
                  searchable
                  disabled={disabled}
                  value={editing.unitId === null ? null : String(editing.unitId)}
                  data={(units.data ?? []).map((unit) => ({
                    value: String(unit.id),
                    label: unit.code,
                  }))}
                  onChange={(value) =>
                    setEditing({ ...editing, unitId: value === null ? null : Number(value) })
                  }
                />
              )}
            </ChoiceField>

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
    queryKey: queryKeys.methodologies.tests(versionId),
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
      await queryClient.invalidateQueries({ queryKey: queryKeys.methodologies.tests(versionId) });
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
  const columns = useColumnDefOptions();

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
            {/* ⛔ Директива "пошук колонки за назвою замість голого
                ColumnDefId": той самий прийом, що вибір довідника в
                `ColumnEditor.tsx` — пошук наскрізь по всіх версіях, бо
                прив'язка не обмежена ОДНІЄЮ таблицею. */}
            {/* ⛔ Той самий запит, та сама ціна, що в обов'язкових входах — і
                саме тому обидва споживачі полагоджені разом: полагодити один
                означало б лишити другий екран із тією самою мовчазною
                порожнечею. */}
            <ChoiceField
              error={columns.error}
              isPending={columns.isPending}
              onRetry={columns.refetch}
            >
              {(disabled) => (
                <Select
                  label={t('methodologies.columnDefId')}
                  description={t('methodologies.columnDefIdHint')}
                  searchable
                  disabled={!editing.isNew || disabled}
                  value={editing.columnDefId === 0 ? null : String(editing.columnDefId)}
                  data={columns.options}
                  onChange={(value) =>
                    setEditing({
                      ...editing,
                      columnDefId: value === null ? 0 : Number(value),
                    })
                  }
                />
              )}
            </ChoiceField>

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
