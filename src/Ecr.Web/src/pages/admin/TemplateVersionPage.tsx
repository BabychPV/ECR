import { useState, type JSX } from 'react';
import {
  Accordion,
  Badge,
  Button,
  Divider,
  Group,
  Modal,
  Stack,
  Table,
  Text,
  TextInput,
} from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useParams } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type {
  CloneVersionRequest,
  DeprecateVersionRequest,
  PublishVersionRequest,
  TemplateColumnDto,
  TemplateStructureDto,
  VersionIdResponse,
} from '@/api/types';
import { AccessMatrix } from '@/features/templates/AccessMatrix';
import { PeriodAccessRuleEditor, PeriodAccessRuleManager } from '@/features/templates/PeriodAccessRuleEditor';
import {
  createPeriodAccessRule,
  deletePeriodAccessRule,
  savePeriodAccessRule,
} from '@/features/templates/periodAccessRuleApi';
import {
  emptyPeriodAccessRuleDraft,
  updatePeriodAccessRuleDraftOf,
  type CreatePeriodAccessRuleDraft,
  type UpdatePeriodAccessRuleDraft,
} from '@/features/templates/periodAccessRule';
import { PresentationEditor } from '@/features/templates/PresentationEditor';
import { SheetEditor } from '@/features/templates/SheetEditor';
import { deleteSheet, saveSheet } from '@/features/templates/sheetApi';
import { draftOf, emptyDraft, type SheetDraft } from '@/features/templates/sheet';
import { TableEditor } from '@/features/templates/TableEditor';
import { deleteTable, saveTable } from '@/features/templates/tableApi';
import {
  draftOf as tableDraftOf,
  emptyDraft as emptyTableDraft,
  type TableDraft,
} from '@/features/templates/table';
import { ColumnEditor } from '@/features/templates/ColumnEditor';
import { deleteColumn, saveColumn } from '@/features/templates/columnApi';
import { columnDraftOf, emptyColumnDraft, type ColumnDefDto, type ColumnDraft } from '@/features/templates/column';
import { RowEditor } from '@/features/templates/RowEditor';
import { deleteRow, saveRow } from '@/features/templates/rowApi';
import { emptyRowDraft, rowDraftOf, type RowDefDto, type RowDraft } from '@/features/templates/row';
import { FormulaEditor } from '@/features/templates/FormulaEditor';
import { saveFormula } from '@/features/templates/formulaApi';
import { emptyFormulaDraft, type FormulaDraft } from '@/features/templates/formula';
import { ValidationRuleEditor } from '@/features/templates/ValidationRuleEditor';
import { deleteValidationRule, saveValidationRule } from '@/features/templates/validationRuleApi';
import { emptyValidationRuleDraft, type ValidationRuleDraft } from '@/features/templates/validationRule';
import { VersionDiff } from '@/features/templates/VersionDiff';
import { localized } from '@/shared/i18n/localized';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { ReasonModal } from '@/shared/ui/ReasonModal';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/**
 * Редактор структури версії.
 *
 * ⛔ Опублікована версія структурно незмінна — це тримає тригер у базі, а не
 * лише інтерфейс. Кнопка публікації ховається не «щоб не заплутати», а тому
 * що сервер однаково відхилить: показана й непрацездатна кнопка гірша за
 * відсутню.
 *
 * ⛔ Шапка (`PageHeader`) рендериться ЗАВЖДИ, включно з версією без жодного
 * аркуша: до цього вона стояла всередині `AsyncBoundary`, і порожня версія
 * лишалася без жодної кнопки взагалі — глухий кут (`S-03`). `AsyncBoundary`
 * тепер стосується лише переліку аркушів і таблиць нижче.
 */
export function TemplateVersionPage(): JSX.Element {
  const { id: templateId, versionId } = useParams();
  const id = Number(versionId);
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const session = useSession();

  const [cloning, setCloning] = useState(false);
  const [newVersion, setNewVersion] = useState('');
  const [publishing, setPublishing] = useState(false);
  const [deprecating, setDeprecating] = useState(false);
  const [editing, setEditing] = useState<TemplateColumnDto | null>(null);
  const [sheetDraft, setSheetDraft] = useState<SheetDraft | null>(null);
  const [formulaDraft, setFormulaDraft] = useState<FormulaDraft | null>(null);

  // ⚠ Чернетка таблиці несе код аркуша окремо від самого `TableDraft`
  // (W5.1): таблиця адресується ДВОМА кодами (`sheets/{sheetCode}/tables/{code}`),
  // а форма керує лише другим — код аркуша задає контекст, у якому її
  // відкрили, і сам не редагується.
  const [tableDraft, setTableDraft] = useState<{ sheetCode: string; draft: TableDraft } | null>(
    null,
  );

  // ⚠ Чернетки колонки й рядка несуть `tableId`: на відміну від аркуша, вони
  // адресуються не лише кодом, а й таблицею-власником (`W5.2`).
  const [columnEdit, setColumnEdit] = useState<{ tableId: number; draft: ColumnDraft } | null>(null);
  const [rowEdit, setRowEdit] = useState<{ tableId: number; draft: RowDraft } | null>(null);

  // ⛔ Кеш повних відповідей ЦЬОГО сеансу, ключ — `tableId:код`. Структура
  // версії (`GET …/structure`) віддає колонку й рядок бідніше, ніж їх приймає
  // й повертає `PUT` (`Q-012`, докладніше в `column.ts`/`row.ts`): без цього
  // кешу повторне відкриття форми правки губило б розширені поля колонки чи
  // переклади підпису рядка, яких структура не носить.
  const [savedColumns, setSavedColumns] = useState<Record<string, ColumnDefDto>>({});
  const [savedRows, setSavedRows] = useState<Record<string, RowDefDto>>({});

  // ⚠ Правило валідації (W5.4) адресується ТАБЛИЦЕЮ: чернетка тримається
  // разом із таблицею, для якої відкрили форму — той самий tableId їде і в
  // PUT, і в DELETE.
  const [validationRuleTable, setValidationRuleTable] = useState<number | null>(null);
  const [validationDraft, setValidationDraft] = useState<ValidationRuleDraft>(
    emptyValidationRuleDraft(),
  );
  const [deleteRuleCode, setDeleteRuleCode] = useState('');

  // ⛔ Правила доступу до періоду (`ФВ-2.15`) не мають коду — форма
  // створення (`createPeriodDraft`) і форма правки наявного за `id`
  // (`manageRuleId`/`manageDraft`) навмисно окремі, за тією самою причиною,
  // що описана в `PeriodAccessRuleEditor.tsx`.
  const [periodRulesOpen, setPeriodRulesOpen] = useState(false);
  const [createPeriodDraft, setCreatePeriodDraft] = useState<CreatePeriodAccessRuleDraft>(
    emptyPeriodAccessRuleDraft(),
  );
  const [manageRuleId, setManageRuleId] = useState<number | null>(null);
  const [manageDraft, setManageDraft] = useState<UpdatePeriodAccessRuleDraft>(
    updatePeriodAccessRuleDraftOf({
      id: 0,
      templateVersionId: id,
      ruleKind: 'AlwaysReadOnly',
      sheetDefId: null,
      tableDefId: null,
      roleId: null,
      rowKind: null,
      fromSequence: null,
      toSequence: null,
      sourceColumnDefId: null,
      relativeOffset: null,
      conditionExpr: null,
      onOutOfWindow: 'ReadOnly',
    }),
  );

  const structure = useQuery({
    queryKey: queryKeys.templates.version(id),
    queryFn: () => apiFetch<TemplateStructureDto>(`/api/v1/template-versions/${id}/structure`),
  });

  /**
   * Публікація версії. Право `Template.Publish`.
   *
   * ⛔ Причина обов'язкова — той самий патерн, що й публікація методології
   * (`MethodologiesPage.tsx`, ФВ-14.7) і сусідня кнопка «вивести з обігу»
   * нижче: до цього поля не було взагалі, і журнал публікацій ніс літерал
   * `"Publish"` — рядок, що ВИГЛЯДАЄ як причина, але однаковий для кожного
   * виклику. За рік «Publish» у журналі не відповідає на питання «чому саме
   * цю версію ввели в обіг».
   */
  const publish = useMutation({
    mutationFn: (reason: string) =>
      apiFetch(`/api/v1/template-versions/${id}/publish`, {
        method: 'POST',
        body: JSON.stringify({ reason } satisfies PublishVersionRequest),
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.templates.version(id) });
      setPublishing(false);
      showDone(t('version.published'));
    },

    // ⚠ Публікація падає з переліком проблем структури: показуємо саме його,
    // а не «не вдалося опублікувати».
    onError: showApiError,
  });

  /**
   * Клон версії (`ФВ-2.8`).
   *
   * ⛔ Єдиний спосіб внести СТРУКТУРНУ зміну в опубліковану версію: вона
   * заморожена тригером у базі, і кнопки «розморозити» не існує навмисно.
   * До аудиту клону не було в інтерфейсі зовсім (`A7-39`), тобто після першої
   * ж публікації шаблон ставав незмінним назавжди.
   *
   * ⚠ `Code` і `RowKey` зберігаються при клонуванні — інакше формули клону
   * посилалися б у порожнечу.
   */
  const clone = useMutation({
    mutationFn: () =>
      apiFetch<VersionIdResponse>(`/api/v1/template-versions/${id}/clone`, {
        method: 'POST',
        body: JSON.stringify({ newVersion: newVersion.trim() } satisfies CloneVersionRequest),
      }),
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.templates.allVersionsOf() });
      setCloning(false);
      setNewVersion('');
      showDone(t('version.cloned'));

      // Одразу відкриваємо клон: інакше користувач лишається на замороженій
      // версії й шукає нову в переліку.
      await navigate(`/admin/templates/${templateId ?? ''}/versions/${result.versionId}`);
    },
    onError: showApiError,
  });

  /**
   * Виведення версії з обігу (`ФВ-7.8`).
   *
   * ⛔ Це відкат БЕЗ видалення: на версію посилаються проєкти, подані
   * форми, зрізи звітності й аудит. Стан `Deprecated` існував від Етапу 1
   * і був недосяжний — перевести версію в нього не міг ніхто, тобто
   * єдиним «відкатом» лишалося видалення.
   *
   * ⚠ Проєкти, прив'язані до цієї версії, працюють далі: інакше відкат
   * зупинив би заповнення форм посеред періоду (`ФВ-1.2`).
   */
  const deprecate = useMutation({
    mutationFn: (reason: string) =>
      apiFetch(`/api/v1/template-versions/${id}/deprecate`, {
        method: 'POST',
        body: JSON.stringify({ reason } satisfies DeprecateVersionRequest),
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.templates.allVersionsOf() });
      setDeprecating(false);
      showDone(t('version.deprecated'));
    },
    onError: showApiError,
  });

  /**
   * Запис аркуша (`ФВ-2.1`) — перший вертикальний зріз авторства структури
   * шаблону через API.
   */
  const saveSheetMutation = useMutation({
    mutationFn: (draft: SheetDraft) => saveSheet(id, draft),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.templates.version(id) });
      setSheetDraft(null);
      showDone(t('sheets.saved'));
    },
    onError: showApiError,
  });

  /** Видалення аркуша — м'яко, `ФВ-7.6`. */
  const deleteSheetMutation = useMutation({
    mutationFn: (code: string) => deleteSheet(id, code),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.templates.version(id) });
      showDone(t('sheets.deleted'));
    },
    onError: showApiError,
  });

  /**
   * Запис таблиці (`W5.1`) — другий вертикальний зріз авторства структури
   * шаблону через API, той самий патерн, що й аркуш вище.
   */
  const saveTableMutation = useMutation({
    mutationFn: (args: { sheetCode: string; draft: TableDraft }) =>
      saveTable(id, args.sheetCode, args.draft),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.templates.version(id) });
      setTableDraft(null);
      showDone(t('tableDef.saved'));
    },
    onError: showApiError,
  });

  /** Видалення таблиці — м'яко, `ФВ-7.6`. */
  const deleteTableMutation = useMutation({
    mutationFn: (args: { sheetCode: string; code: string }) =>
      deleteTable(id, args.sheetCode, args.code),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.templates.version(id) });
      showDone(t('tableDef.deleted'));
    },
    onError: showApiError,
  });

  /**
   * Запис колонки (`W5.2`) — третій вертикальний зріз авторства структури
   * шаблону через API, за зразком запису аркуша й таблиці вище.
   */
  const saveColumnMutation = useMutation({
    mutationFn: ({ tableId, draft }: { tableId: number; draft: ColumnDraft }) =>
      saveColumn(id, tableId, draft),
    onSuccess: async (result, variables) => {
      // ⛔ Кладемо ПОВНУ відповідь у кеш сеансу до інвалідації запиту: інакше
      // наступне відкриття форми правки цієї-таки колонки знову побачило б
      // лише бідний `TemplateColumnDto` зі структури.
      setSavedColumns((prev) => ({ ...prev, [`${String(variables.tableId)}:${result.code}`]: result }));
      await queryClient.invalidateQueries({ queryKey: queryKeys.templates.version(id) });
      setColumnEdit(null);
      showDone(t('columns.saved'));
    },
    onError: showApiError,
  });

  /** Видалення колонки — м'яко, `ФВ-7.6`. */
  const deleteColumnMutation = useMutation({
    mutationFn: ({ tableId, code }: { tableId: number; code: string }) => deleteColumn(id, tableId, code),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.templates.version(id) });
      showDone(t('columns.deleted'));
    },
    onError: showApiError,
  });

  /**
   * Запис рядка (`W5.2`) — четвертий вертикальний зріз авторства структури
   * шаблону через API.
   */
  const saveRowMutation = useMutation({
    mutationFn: ({ tableId, draft }: { tableId: number; draft: RowDraft }) => saveRow(id, tableId, draft),
    onSuccess: async (result, variables) => {
      setSavedRows((prev) => ({ ...prev, [`${String(variables.tableId)}:${result.rowKey}`]: result }));
      await queryClient.invalidateQueries({ queryKey: queryKeys.templates.version(id) });
      setRowEdit(null);
      showDone(t('rows.saved'));
    },
    onError: showApiError,
  });

  /** Видалення рядка — м'яко, `ФВ-7.6`. */
  const deleteRowMutation = useMutation({
    mutationFn: ({ tableId, rowKey }: { tableId: number; rowKey: string }) => deleteRow(id, tableId, rowKey),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.templates.version(id) });
      showDone(t('rows.deleted'));
    },
    onError: showApiError,
  });

  /**
   * Запис формули колонки чи рядка (`W5.3`) — наступний зріз авторства
   * структури шаблону через API, за зразком аркуша вище.
   *
   * ⚠ Структура версії (`GET …/structure`) поки не показує наявних формул —
   * той самий рід пропуску, що й `SheetDto.tables` у W5.0 («заводить наступний
   * зріз»): додати `Formulas` до `TableDto` означало б правити
   * `TemplateStructureDto.cs`/`GetTemplateStructureHandler.cs`, а вони поза
   * межами цього зрізу (SCOPE). Тому кнопка нижче завжди відкриває ПОРОЖНЮ
   * форму: побачити наявний текст формули, не пам'ятаючи його, поки не можна —
   * записати новий (що замінить старий) можна.
   */
  const saveFormulaMutation = useMutation({
    mutationFn: (draft: FormulaDraft) => saveFormula(id, draft),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.templates.version(id) });
      setFormulaDraft(null);
      showDone(t('formulas.saved'));
    },
    onError: showApiError,
  });


  /**
   * Запис правила валідації таблиці (W5.4, продовження `ФВ-2.1` на
   * `ValidationRule`).
   */
  const saveValidationRuleMutation = useMutation({
    mutationFn: ({ tableId, draft }: { tableId: number; draft: ValidationRuleDraft }) =>
      saveValidationRule(id, tableId, draft),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.templates.version(id) });
      setValidationRuleTable(null);
      showDone(t('validationRules.saved'));
    },
    onError: showApiError,
  });

  /** Видалення правила валідації — фізичне (на відміну від аркуша). */
  const deleteValidationRuleMutation = useMutation({
    mutationFn: ({ tableId, code }: { tableId: number; code: string }) =>
      deleteValidationRule(id, tableId, code),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.templates.version(id) });
      setDeleteRuleCode('');
      showDone(t('validationRules.deleted'));
    },
    onError: showApiError,
  });

  /**
   * Заводить нове правило доступу до періоду (`ФВ-2.15`).
   *
   * ⛔ `POST`, а не `PUT` за кодом: `PeriodAccessRuleDef` не має природного
   * коду (`periodAccessRule.ts`).
   */
  const createPeriodRuleMutation = useMutation({
    mutationFn: (draft: CreatePeriodAccessRuleDraft) => createPeriodAccessRule(id, draft),
    onSuccess: async (created) => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.templates.version(id) });
      setCreatePeriodDraft(emptyPeriodAccessRuleDraft());

      // ⚠ Щойно створене правило одразу підставляється у форму «правка за
      // id» нижче: інакше побачений на екрані id довелося б передруковувати
      // руками, щоб одразу ж його відредагувати чи прибрати.
      setManageRuleId(created.id);
      setManageDraft(updatePeriodAccessRuleDraftOf(created));
      showDone(t('periodRules.added'));
    },
    onError: showApiError,
  });

  /** Змінює прив'язку й поведінку наявного правила доступу до періоду. */
  const savePeriodRuleMutation = useMutation({
    mutationFn: ({ ruleId, draft }: { ruleId: number; draft: UpdatePeriodAccessRuleDraft }) =>
      savePeriodAccessRule(id, ruleId, draft),
    onSuccess: async () => {
      showDone(t('periodRules.saved'));
    },
    onError: showApiError,
  });

  /** Прибирає правило доступу до періоду (фізично). */
  const deletePeriodRuleMutation = useMutation({
    mutationFn: (ruleId: number) => deletePeriodAccessRule(id, ruleId),
    onSuccess: async () => {
      setManageRuleId(null);
      showDone(t('periodRules.deleted'));
    },
    onError: showApiError,
  });
  // ⚠ Структура не несе статусу версії: його віддає перелік версій шаблону.
  // Тому кнопка публікації тут показується за правом, а сервер лишається
  // єдиним, хто вирішує, чи можна публікувати саме цю версію.
  const editable = true;

  // ⛔ Форма аркуша, на відміну від кнопки публікації вище, ХОВАЄТЬСЯ на
  // опублікованій версії: `isEditable` рахує СЕРВЕР (той самий прапорець,
  // що й `TableRelationsDto.isEditable` на сторінці зв'язків) — інакше
  // показана форма означала б обіцянку, яку `ECR-TMPL-0409` однаково не
  // виконає.
  const canEditSheets = can(session.data, 'Template.Edit') && (structure.data?.isEditable ?? false);

  const nextOrdinal = (() => {
    const sheets = structure.data?.sheets ?? [];
    return sheets.length === 0 ? 0 : Math.max(...sheets.map((s) => s.ordinal)) + 1;
  })();

  return (
    <>
      <PageHeader
        title={`${t('version.title')} ${String(id)}`}
        actions={
          <Group gap="xs">
            {structure.data !== undefined && (
              <Text size="xs" c="dimmed">
                r{structure.data.presentationRevision}
              </Text>
            )}
            {/* ⚠ Порівняння версій доступне за правом ПЕРЕГЛЯДУ: питання
                «що зміниться» законне й для того, хто нічого не править —
                саме з нього починається рішення про міграцію. */}
            {can(session.data, 'Template.View') && <VersionDiff templateVersionId={id} />}

            {/* ⛔ Матриця доступу (`ФВ-2.18`) — теж право ПЕРЕГЛЯДУ: питання
                «які періоди відкриті на цьому аркуші» законне для всіх, і
                саме воно найчастіше й з'ясовується постфактум, коли форму
                вже не заповнити. */}
            {can(session.data, 'Template.View') && <AccessMatrix templateVersionId={id} />}

            {/* ⛔ Правила доступу до періоду (`ФВ-2.15`, W5.4) — кнопка тут
                же, поруч із матрицею: саме матриця показує НАСЛІДОК цих
                правил, і питання «чому тут замок» найчастіше веде до «а що
                за ним налаштовано». */}
            {canEditSheets && (
              <Button size="xs" variant="default" onClick={() => setPeriodRulesOpen(true)}>
                {t('periodRules.title')}
              </Button>
            )}

            {/* ⛔ Зв'язки таблиць (`ФВ-2.12`, `ФВ-2.13`) — окрема сторінка, і
                вхід у неї стоїть саме тут: питання «звідки в цій таблиці
                числа» ставлять, дивлячись на структуру. Без цього посилання
                редактор існував би лише за адресою, яку треба знати. */}
            {can(session.data, 'Template.View') && (
              <Button
                size="xs"
                variant="default"
                onClick={() =>
                  void navigate(
                    `/admin/templates/${templateId ?? ''}/versions/${String(id)}/relations`,
                  )
                }
              >
                {t('version.relations')}
              </Button>
            )}

            {/* ⛔ Клон — єдиний спосіб змінити структуру після публікації
                (`ФВ-7.1`). Кнопка є завжди, коли є право правити шаблони:
                клонувати чернетку теж законно. */}
            {can(session.data, 'Template.Edit') && (
              <Button size="xs" variant="default" onClick={() => setCloning(true)}>
                {t('version.clone')}
              </Button>
            )}

            {editable && can(session.data, 'Template.Publish') && (
              <>
                <Button size="xs" onClick={() => setPublishing(true)}>
                  {t('version.publish')}
                </Button>

                {/* ⚠ Право те саме, що на публікацію: вивести з обігу —
                    рішення тієї самої ваги, що й випустити. Сервер
                    відмовить, якщо версія ще чернетка. */}
                <Button
                  size="xs"
                  variant="default"
                  color="statusError"
                  onClick={() => setDeprecating(true)}
                >
                  {t('version.deprecate')}
                </Button>
              </>
            )}
          </Group>
        }
      />

      {/*
       * ⚠ Версія без аркушів — окремий стан: опублікувати таку не можна, і
       * дізнатися про це з відмови публікації гірше, ніж побачити на екрані.
       * `emptyAction` дає ЗІ ВХОДУ вихід із цього стану: кнопка «додати
       * аркуш» показана й тут — інакше порожня версія лишалася б глухим
       * кутом (`S-03`), бо самого переліку немає, чого показувати.
       */}
      <AsyncBoundary<TemplateStructureDto>
        isPending={structure.isPending}
        error={structure.error}
        data={structure.data}
        isEmpty={(version) => version.sheets.length === 0}
        emptyTitle={t('version.empty')}
        emptyHint={t('version.emptyHint')}
        emptyAction={
          canEditSheets && (
            <Button onClick={() => setSheetDraft(emptyDraft(0))}>{t('sheets.add')}</Button>
          )
        }
        skeleton="form"
        onRetry={() => void structure.refetch()}
      >
        {(version) => (
          <>
            {canEditSheets && (
              <Group justify="flex-end" mb="xs">
                <Button
                  variant="default"
                  onClick={() => setSheetDraft(emptyDraft(nextOrdinal))}
                >
                  {t('sheets.add')}
                </Button>
              </Group>
            )}

            <Accordion multiple>
              {[...version.sheets]
                .sort((a, b) => a.ordinal - b.ordinal)
                .map((sheet) => (
                  <Accordion.Item key={sheet.id} value={sheet.code}>
                    <Accordion.Control>
                      {localized(sheet.nameL10n)}{' '}
                      <Text span c="dimmed">
                        ({sheet.code})
                      </Text>
                      {!sheet.isVisible && (
                        <Badge ml="xs" size="xs" variant="outline">
                          {t('version.hidden')}
                        </Badge>
                      )}
                    </Accordion.Control>
                    <Accordion.Panel>
                      {canEditSheets && (
                        <Group gap="xs" mb="sm">
                          <Button
                            size="compact-xs"
                            variant="subtle"
                            onClick={() => setSheetDraft(draftOf(sheet))}
                          >
                            {t('sheets.edit')}
                          </Button>
                          <Button
                            size="compact-xs"
                            variant="subtle"
                            color="statusError"
                            loading={deleteSheetMutation.isPending}
                            onClick={() => deleteSheetMutation.mutate(sheet.code)}
                          >
                            {t('sheets.delete')}
                          </Button>
                        </Group>
                      )}

                      {canEditSheets && (
                        <Group justify="flex-end" mb="xs">
                          <Button
                            size="compact-xs"
                            variant="default"
                            onClick={() =>
                              setTableDraft({
                                sheetCode: sheet.code,
                                draft: emptyTableDraft(
                                  sheet.tables.length === 0
                                    ? 0
                                    : Math.max(...sheet.tables.map((t2) => t2.ordinal)) + 1,
                                ),
                              })
                            }
                          >
                            {t('tableDef.add')}
                          </Button>
                        </Group>
                      )}

                      {sheet.tables.map((table) => {
                        const nextColumnOrdinal =
                          table.columns.length === 0
                            ? 0
                            : Math.max(...table.columns.map((c) => c.ordinal)) + 1;
                        const nextRowOrdinal =
                          table.rows.length === 0 ? 0 : Math.max(...table.rows.map((r) => r.ordinal)) + 1;

                        return (
                          <div key={table.id}>
                            <Group justify="space-between" mt="sm">
                              <Text fw={600}>
                                {localized(table.nameL10n) || table.code}{' '}
                                <Text span c="dimmed">
                                  ({table.code}) · {table.rowMode}
                                </Text>
                              </Text>
                              {canEditSheets && (
                                <Group gap="xs">
                                  <Button
                                    size="compact-xs"
                                    variant="subtle"
                                    onClick={() =>
                                      setTableDraft({
                                        sheetCode: sheet.code,
                                        draft: tableDraftOf(table),
                                      })
                                    }
                                  >
                                    {t('tableDef.edit')}
                                  </Button>
                                  <Button
                                    size="compact-xs"
                                    variant="subtle"
                                    color="statusError"
                                    loading={
                                      deleteTableMutation.isPending &&
                                      deleteTableMutation.variables?.code === table.code
                                    }
                                    onClick={() =>
                                      deleteTableMutation.mutate({
                                        sheetCode: sheet.code,
                                        code: table.code,
                                      })
                                    }
                                  >
                                    {t('tableDef.delete')}
                                  </Button>
                                  <Button
                                    size="compact-xs"
                                    variant="default"
                                    onClick={() =>
                                      setColumnEdit({ tableId: table.id, draft: emptyColumnDraft(nextColumnOrdinal) })
                                    }
                                  >
                                    {t('columns.add')}
                                  </Button>
                                  {/* ⛔ Правило валідації (W5.4, продовження
                                      `ФВ-2.1` на `ValidationRule`) адресується
                                      ТАБЛИЦЕЮ — кнопка тому стоїть тут, а не
                                      на рівні аркуша чи версії. */}
                                  <Button
                                    size="compact-xs"
                                    variant="subtle"
                                    onClick={() => {
                                      setValidationRuleTable(table.id);
                                      setValidationDraft(emptyValidationRuleDraft());
                                      setDeleteRuleCode('');
                                    }}
                                  >
                                    {t('validationRules.title')}
                                  </Button>
                                </Group>
                              )}
                            </Group>
                            <Table striped withTableBorder mt="xs">
                              <Table.Thead>
                                <Table.Tr>
                                  <Table.Th>{t('version.column')}</Table.Th>
                                  <Table.Th>{t('version.type')}</Table.Th>
                                  <Table.Th>{t('version.unit')}</Table.Th>
                                  <Table.Th />
                                </Table.Tr>
                              </Table.Thead>
                              <Table.Tbody>
                                {table.columns.map((column) => (
                                  <Table.Tr key={column.id}>
                                    <Table.Td>
                                      {localized(column.headerL10n) || column.code}{' '}
                                      <Text span c="dimmed">
                                        ({column.code})
                                      </Text>
                                      {column.isHidden && (
                                        <Badge ml="xs" size="xs" variant="outline">
                                          {t('version.hidden')}
                                        </Badge>
                                      )}
                                    </Table.Td>
                                    <Table.Td>
                                      {column.dataType}
                                      {column.isReadOnly && (
                                        <Badge ml="xs" size="xs" variant="light">
                                          {t('version.readOnly')}
                                        </Badge>
                                      )}
                                    </Table.Td>
                                    <Table.Td>{column.unitSymbol ?? '—'}</Table.Td>
                                    <Table.Td>
                                      <Group gap="xs" wrap="nowrap" justify="flex-end">
                                        {/* ⚠ Правка тут не потребує нової версії: підпис,
                                            порядок, формат і видимість — презентаційний
                                            шар, і його дозволено міняти в опублікованій
                                            версії (`ФВ-7.2`). */}
                                        {can(session.data, 'Template.Edit') && (
                                          <Button
                                            size="compact-xs"
                                            variant="subtle"
                                            onClick={() => setEditing(column)}
                                          >
                                            {t('version.presentation')}
                                          </Button>
                                        )}
                                        {canEditSheets && (
                                          <>
                                            <Button
                                              size="compact-xs"
                                              variant="subtle"
                                              onClick={() =>
                                                setColumnEdit({
                                                  tableId: table.id,
                                                  draft: columnDraftOf(
                                                    column,
                                                    savedColumns[`${String(table.id)}:${column.code}`],
                                                  ),
                                                })
                                              }
                                            >
                                              {t('columns.edit')}
                                            </Button>
                                            <Button
                                              size="compact-xs"
                                              variant="subtle"
                                              color="statusError"
                                              loading={
                                                deleteColumnMutation.isPending
                                                && deleteColumnMutation.variables?.code === column.code
                                              }
                                              onClick={() =>
                                                deleteColumnMutation.mutate({ tableId: table.id, code: column.code })
                                              }
                                            >
                                              {t('columns.delete')}
                                            </Button>
                                            <Button
                                              size="compact-xs"
                                              variant="subtle"
                                              onClick={() =>
                                                setFormulaDraft(
                                                  emptyFormulaDraft(table.id, 'Column', String(column.id)),
                                                )
                                              }
                                            >
                                              {t('formulas.edit')}
                                            </Button>
                                          </>
                                        )}
                                      </Group>
                                    </Table.Td>
                                  </Table.Tr>
                                ))}
                              </Table.Tbody>
                            </Table>

                            {/* ⚠ Рядки фіксованої таблиці. Динамічна (`RowMode.Dynamic`)
                                не показує тут нічого й додати рядок не дає:
                                домен (`TableDef.AddRow`) відхиляє їх шаблоном,
                                вони з'являються під час роботи, а не тут. */}
                            {table.rowMode !== 'Dynamic' && (
                              <>
                                <Group justify="space-between" mt="sm">
                                  <Text fw={600} size="sm" c="dimmed">
                                    {t('rows.title')}
                                  </Text>
                                  {canEditSheets && (
                                    <Button
                                      size="compact-xs"
                                      variant="default"
                                      onClick={() =>
                                        setRowEdit({ tableId: table.id, draft: emptyRowDraft(nextRowOrdinal) })
                                      }
                                    >
                                      {t('rows.add')}
                                    </Button>
                                  )}
                                </Group>

                                {table.rows.length === 0 ? (
                                  <Text size="sm" c="dimmed">
                                    {t('rows.empty')}
                                  </Text>
                                ) : (
                                  <Table striped withTableBorder mt="xs">
                                    <Table.Thead>
                                      <Table.Tr>
                                        <Table.Th>{t('rows.label')}</Table.Th>
                                        <Table.Th>{t('rows.rowKind')}</Table.Th>
                                        <Table.Th />
                                      </Table.Tr>
                                    </Table.Thead>
                                    <Table.Tbody>
                                      {table.rows.map((row) => (
                                        <Table.Tr key={row.rowKey}>
                                          <Table.Td>
                                            {row.label ?? row.rowKey}{' '}
                                            <Text span c="dimmed">
                                              ({row.rowKey})
                                            </Text>
                                          </Table.Td>
                                          <Table.Td>{row.rowKind}</Table.Td>
                                          <Table.Td>
                                            {canEditSheets && (
                                              <Group gap="xs" wrap="nowrap" justify="flex-end">
                                                <Button
                                                  size="compact-xs"
                                                  variant="subtle"
                                                  onClick={() =>
                                                    setRowEdit({
                                                      tableId: table.id,
                                                      draft: rowDraftOf(
                                                        row,
                                                        savedRows[`${String(table.id)}:${row.rowKey}`],
                                                      ),
                                                    })
                                                  }
                                                >
                                                  {t('rows.edit')}
                                                </Button>
                                                <Button
                                                  size="compact-xs"
                                                  variant="subtle"
                                                  color="statusError"
                                                  loading={
                                                    deleteRowMutation.isPending
                                                    && deleteRowMutation.variables?.rowKey === row.rowKey
                                                  }
                                                  onClick={() =>
                                                    deleteRowMutation.mutate({ tableId: table.id, rowKey: row.rowKey })
                                                  }
                                                >
                                                  {t('rows.delete')}
                                                </Button>
                                                <Button
                                                  size="compact-xs"
                                                  variant="subtle"
                                                  onClick={() =>
                                                    setFormulaDraft(emptyFormulaDraft(table.id, 'Row', row.rowKey))
                                                  }
                                                >
                                                  {t('formulas.edit')}
                                                </Button>
                                              </Group>
                                            )}
                                          </Table.Td>
                                        </Table.Tr>
                                      ))}
                                    </Table.Tbody>
                                  </Table>
                                )}
                              </>
                            )}
                          </div>
                        );
                      })}
                    </Accordion.Panel>
                  </Accordion.Item>
                ))}
            </Accordion>
          </>
        )}
      </AsyncBoundary>

      <PresentationEditor
        templateVersionId={id}
        column={editing}
        onClose={() => setEditing(null)}
      />

      <ReasonModal
        opened={publishing}
        title={t('version.publish')}
        label={t('workflow.reason')}
        description={t('version.publishHint')}
        confirmLabel={t('version.publish')}
        isPending={publish.isPending}
        onConfirm={(reason) => publish.mutate(reason)}
        onClose={() => setPublishing(false)}
      />

      <ReasonModal
        opened={deprecating}
        title={t('version.deprecate')}
        label={t('workflow.reason')}
        description={t('version.deprecateHint')}
        confirmLabel={t('version.deprecate')}
        isPending={deprecate.isPending}
        onConfirm={(reason) => deprecate.mutate(reason)}
        onClose={() => setDeprecating(false)}
      />

      <Modal opened={cloning} onClose={() => setCloning(false)} title={t('version.clone')}>
        <TextInput
          label={t('templates.versionNumber')}
          description={t('version.cloneHint')}
          value={newVersion}
          onChange={(event) => setNewVersion(event.currentTarget.value)}
          data-autofocus
        />

        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setCloning(false)}>
            {t('common.cancel')}
          </Button>
          <Button
            disabled={newVersion.trim().length === 0}
            loading={clone.isPending}
            onClick={() => clone.mutate()}
          >
            {t('version.clone')}
          </Button>
        </Group>
      </Modal>

      <Modal
        opened={sheetDraft !== null}
        onClose={() => setSheetDraft(null)}
        title={sheetDraft?.isNew === true ? t('sheets.add') : t('sheets.edit')}
      >
        {sheetDraft !== null && (
          <SheetEditor
            draft={sheetDraft}
            disabled={!canEditSheets}
            saving={saveSheetMutation.isPending}
            onChange={setSheetDraft}
            onSubmit={() => saveSheetMutation.mutate(sheetDraft)}
            onCancel={() => setSheetDraft(null)}
          />
        )}
      </Modal>

      <Modal
        opened={tableDraft !== null}
        onClose={() => setTableDraft(null)}
        title={tableDraft?.draft.isNew === true ? t('tableDef.add') : t('tableDef.edit')}
      >
        {tableDraft !== null && (
          <TableEditor
            draft={tableDraft.draft}
            disabled={!canEditSheets}
            saving={saveTableMutation.isPending}
            onChange={(draft) => setTableDraft({ sheetCode: tableDraft.sheetCode, draft })}
            onSubmit={() => saveTableMutation.mutate(tableDraft)}
            onCancel={() => setTableDraft(null)}
          />
        )}
      </Modal>

      <Modal
        opened={columnEdit !== null}
        onClose={() => setColumnEdit(null)}
        title={columnEdit?.draft.isNew === true ? t('columns.add') : t('columns.edit')}
      >
        {columnEdit !== null && (
          <ColumnEditor
            draft={columnEdit.draft}
            disabled={!canEditSheets}
            saving={saveColumnMutation.isPending}
            onChange={(draft) => setColumnEdit({ tableId: columnEdit.tableId, draft })}
            onSubmit={() => saveColumnMutation.mutate(columnEdit)}
            onCancel={() => setColumnEdit(null)}
          />
        )}
      </Modal>

      <Modal
        opened={rowEdit !== null}
        onClose={() => setRowEdit(null)}
        title={rowEdit?.draft.isNew === true ? t('rows.add') : t('rows.edit')}
      >
        {rowEdit !== null && (
          <RowEditor
            draft={rowEdit.draft}
            disabled={!canEditSheets}
            saving={saveRowMutation.isPending}
            onChange={(draft) => setRowEdit({ tableId: rowEdit.tableId, draft })}
            onSubmit={() => saveRowMutation.mutate(rowEdit)}
            onCancel={() => setRowEdit(null)}
          />
        )}
      </Modal>

      <Modal
        opened={formulaDraft !== null}
        onClose={() => setFormulaDraft(null)}
        title={t('formulas.edit')}
        size="lg"
      >
        {formulaDraft !== null && (
          <FormulaEditor
            draft={formulaDraft}
            templateVersionId={id}
            disabled={!canEditSheets}
            saving={saveFormulaMutation.isPending}
            onChange={setFormulaDraft}
            onSubmit={() => saveFormulaMutation.mutate(formulaDraft)}
            onCancel={() => setFormulaDraft(null)}
          />
        )}
      </Modal>

      {/*
       * ⛔ Правило валідації (W5.4). Форма PUT-за-кодом стоїть поруч із
       * компактним видаленням за тим самим кодом: список наявних правил
       * структура версії не несе (він живе лише в кешованому знімку, який
       * читає рушій валідації, а не GET-відповідь), тому редагування — це
       * ввести код і перезаписати, а не обрати рядок зі списку.
       */}
      <Modal
        opened={validationRuleTable !== null}
        onClose={() => setValidationRuleTable(null)}
        title={t('validationRules.title')}
      >
        {validationRuleTable !== null && (
          <Stack gap="md">
            <ValidationRuleEditor
              draft={validationDraft}
              disabled={!canEditSheets}
              saving={saveValidationRuleMutation.isPending}
              onChange={setValidationDraft}
              onSubmit={() =>
                saveValidationRuleMutation.mutate({
                  tableId: validationRuleTable,
                  draft: validationDraft,
                })
              }
              onCancel={() => setValidationRuleTable(null)}
            />

            <Divider label={t('validationRules.delete')} />

            <Group align="flex-end">
              <TextInput
                label={t('validationRules.code')}
                value={deleteRuleCode}
                onChange={(event) => setDeleteRuleCode(event.currentTarget.value)}
              />
              <Button
                color="statusError"
                variant="default"
                disabled={!canEditSheets || deleteRuleCode.trim().length === 0}
                loading={deleteValidationRuleMutation.isPending}
                onClick={() =>
                  deleteValidationRuleMutation.mutate({
                    tableId: validationRuleTable,
                    code: deleteRuleCode.trim(),
                  })
                }
              >
                {t('validationRules.delete')}
              </Button>
            </Group>
          </Stack>
        )}
      </Modal>

      {/*
       * ⛔ Правила доступу до періоду (`ФВ-2.15`, W5.4). Дві форми в одному
       * вікні — заведення нового (`POST`) і правка/видалення наявного за
       * `id` (`PUT`/`DELETE`) — з тієї самої причини, що описана в
       * `PeriodAccessRuleEditor.tsx`: сутність не має коду, і адресувати
       * наявний рядок можна лише тим, що вже показав сервер.
       */}
      <Modal
        opened={periodRulesOpen}
        onClose={() => setPeriodRulesOpen(false)}
        title={t('periodRules.title')}
        size="lg"
      >
        <Stack gap="lg">
          <PeriodAccessRuleEditor
            draft={createPeriodDraft}
            disabled={!canEditSheets}
            saving={createPeriodRuleMutation.isPending}
            onChange={setCreatePeriodDraft}
            onSubmit={() => createPeriodRuleMutation.mutate(createPeriodDraft)}
          />

          <Divider label={t('periodRules.manage')} />

          <PeriodAccessRuleManager
            ruleId={manageRuleId}
            draft={manageDraft}
            disabled={!canEditSheets}
            saving={savePeriodRuleMutation.isPending || deletePeriodRuleMutation.isPending}
            onRuleIdChange={setManageRuleId}
            onChange={setManageDraft}
            onSave={() => {
              if (manageRuleId !== null) {
                savePeriodRuleMutation.mutate({ ruleId: manageRuleId, draft: manageDraft });
              }
            }}
            onDelete={() => {
              if (manageRuleId !== null) {
                deletePeriodRuleMutation.mutate(manageRuleId);
              }
            }}
          />
        </Stack>
      </Modal>
    </>
  );
}
