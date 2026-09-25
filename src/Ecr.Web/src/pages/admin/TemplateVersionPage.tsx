import { Suspense, lazy, useState, type JSX } from 'react';
import {
  Accordion,
  Alert,
  Badge,
  Button,
  Divider,
  Group,
  Modal,
  Skeleton,
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
  RoleView,
  DeprecateVersionRequest,
  PublishVersionRequest,
  TemplateColumnDto,
  TemplateStructureDto,
  TemplateVersionPage as TemplateVersionPageDto,
  VersionIdResponse,
} from '@/api/types';
import { AccessMatrix } from '@/features/templates/AccessMatrix';
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
import { deleteSheet, saveSheet } from '@/features/templates/sheetApi';
import { draftOf, emptyDraft, type SheetDraft } from '@/features/templates/sheet';
import { deleteTable, saveTable } from '@/features/templates/tableApi';
import {
  draftOf as tableDraftOf,
  emptyDraft as emptyTableDraft,
  type TableDraft,
} from '@/features/templates/table';
import { deleteColumn, saveColumn } from '@/features/templates/columnApi';
import { emptyColumnDraft, type ColumnDraft } from '@/features/templates/column';
import { ExistingColumn } from '@/features/templates/ExistingColumn';
import { dataTypeLabel, rowKindLabel, rowModeLabel } from '@/features/templates/enumLabels';
import { getHeaderFields, saveHeaderField } from '@/features/templates/headerFieldApi';
import {
  emptyHeaderFieldDraft,
  headerFieldDraftOf,
  type HeaderFieldDraft,
} from '@/features/templates/headerField';
import { deleteRow, saveRow } from '@/features/templates/rowApi';
import { emptyRowDraft, rowDraftOf, type RowDefDto, type RowDraft } from '@/features/templates/row';
import { saveFormula } from '@/features/templates/formulaApi';
import { draftOfFormula, emptyFormulaDraft, type FormulaDraft } from '@/features/templates/formula';
import {
  deleteValidationRule,
  saveValidationRule,
  validationRulesKey,
} from '@/features/templates/validationRuleApi';
import { emptyValidationRuleDraft, type ValidationRuleDraft } from '@/features/templates/validationRule';
import { LocalDraft } from '@/features/templates/LocalDraft';
import { VersionDiff } from '@/features/templates/VersionDiff';
import { LazyTableSlots, estimateTemplateTableHeight } from '@/features/templates/LazyTableSlots';
import { localized } from '@/shared/i18n/localized';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { ReasonModal } from '@/shared/ui/ReasonModal';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/**
 * Редактори, що відкриваються ЛИШЕ дією — за `import()`.
 *
 * ⛔ Причина не в тому, що #388 додав кілограм спільного коду. Маршрут мав
 * **нуль запасу**: 249.8 КБ зі стелі 250 (`D-132`). Будь-який приріст
 * спільного коду — `AsyncBoundary`, `notify`, `PageHeader`, байдуже чий —
 * ламав його наступного разу так само. Підняти межу означало б прибрати
 * термометр замість причини.
 *
 * ⚠ Що саме винесено і чому це чесно: кожен із цих редакторів монтується
 * лише всередині `<Modal>`, і кожна модалка вже стоїть за станом
 * (`sheetDraft !== null`, `periodRulesOpen` тощо). Тобто РАНІШЕ їх код
 * завантажували всі, хто відкривав сторінку, а показували — одиниці, що
 * натиснули кнопку. `import()` не міняє ні моменту монтування, ні поведінки —
 * лише момент завантаження байтів.
 *
 * ⚠ `fallback={null}` — свідомо. Рамку діалогу з заголовком малює сама
 * `Modal`, тобто дія користувача вже має видимий відгук; чанк приходить із
 * того самого походження за десятки мілісекунд. Копія вмісту в заглушці була
 * б гіршою за порожнечу: підміна заглушки справжньою формою перемонтовує
 * піддерево і скидає щойно введене.
 *
 * ⛔ Одна межа — ОДИН компонент, і межа існує лише поки діалог відкритий. Це
 * та сама форма, що вже працює в `ColumnEditor.tsx` (лінивий `StyleEditor`),
 * і навмисно НЕ та, що дала livelock у #295: там під однією межею стояла 91
 * лінива сітка (`SheetTables.tsx`).
 */
const ColumnEditor = lazy(async () => ({
  default: (await import('@/features/templates/ColumnEditor')).ColumnEditor,
}));

/**
 * Редактор поля шапки документа (рівень усього документа, не таблиці) — той
 * самий лінивий патерн, що `ColumnEditor` вище, і з тієї самої причини
 * (`D-132`, коментар над `ColumnEditor`): монтується лише всередині своєї
 * `<Modal>`, чанк вантажиться, коли форму справді відкрили.
 */
const HeaderFieldEditor = lazy(async () => ({
  default: (await import('@/features/templates/HeaderFieldEditor')).HeaderFieldEditor,
}));

const SheetEditor = lazy(async () => ({
  default: (await import('@/features/templates/SheetEditor')).SheetEditor,
}));

const TableEditor = lazy(async () => ({
  default: (await import('@/features/templates/TableEditor')).TableEditor,
}));

const RowEditor = lazy(async () => ({
  default: (await import('@/features/templates/RowEditor')).RowEditor,
}));

const FormulaEditor = lazy(async () => ({
  default: (await import('@/features/templates/FormulaEditor')).FormulaEditor,
}));

const ValidationRuleEditor = lazy(async () => ({
  default: (await import('@/features/templates/ValidationRuleEditor')).ValidationRuleEditor,
}));

// ⚠ Два експорти одного модуля — два `lazy`, але ОДИН чанк: обидва `import()`
// вказують на той самий шлях, і Rollup зводить їх до одного файла, а
// рантайм — до одного завантаження.
const PeriodAccessRuleEditor = lazy(async () => ({
  default: (await import('@/features/templates/PeriodAccessRuleEditor')).PeriodAccessRuleEditor,
}));

const PeriodAccessRuleManager = lazy(async () => ({
  default: (await import('@/features/templates/PeriodAccessRuleEditor')).PeriodAccessRuleManager,
}));

const ValidationRuleList = lazy(async () => ({
  default: (await import('@/features/templates/ValidationRuleList')).ValidationRuleList,
}));

const TemplateColumnUsage = lazy(async () => ({
  default: (await import('@/features/templates/ColumnUsage')).TemplateColumnUsage,
}));

/**
 * ⛔ `PresentationEditor` — єдиний із цих редакторів, що НЕ стоїть за
 * `{умова && …}`: він сам носить усередині `<Modal opened={column !== null}>`.
 * Тому гейт тут окремий — і він ОДНОСТОРОННІЙ (`presentationUsed`), а не
 * `editing !== null`. Різниця не косметична: гейт `editing !== null` знімав би
 * компонент із дерева в ту саму мить, коли діалог починає закриватися, тобто
 * вбивав би анімацію закриття й робив би `lazy` видимою зміною поведінки.
 * Односторонній прапорець дає рівно те, що було: доки не відкривали — у дереві
 * нічого (закрита `Modal` і так не рендерить ані рамки), відкрили один раз —
 * далі як раніше.
 */
const PresentationEditor = lazy(async () => ({
  default: (await import('@/features/templates/PresentationEditor')).PresentationEditor,
}));

/** Стан форми «правка правила доступу за id». */
type TemplateTable = TemplateStructureDto['sheets'][number]['tables'][number];

/**
 * Назва таблиці в дереві структури — ОДНА на заповнювач і змонтовану
 * таблицю (`LazyTableSlots`): розмітка не стрибає в мить монтування.
 */
function TableTitle({ table }: { table: TemplateTable }): JSX.Element {
  return (
    <>
      {localized(table.nameL10n) || table.code}{' '}
      <Text span c="dimmed">
        ({table.code}) · {rowModeLabel(table.rowMode)}
      </Text>
    </>
  );
}

/**
 * Що саме чекає підтвердження видалення (R-06/X-01, четвертий раунд UX).
 *
 * ⛔ Доти аркуш, таблиця, колонка, рядок і правило валідації видалялися ОДНИМ
 * натисканням, без жодного питання, — а кнопка «Remove» стоїть поруч з
 * «Edit», за піксель від звичайної дії. Одне підтвердження на сторінку, а не
 * по одному на рядок: відкритим буває лише одне.
 */
type DeleteTarget =
  | { readonly kind: 'sheet'; readonly code: string; readonly name: string }
  | { readonly kind: 'table'; readonly sheetCode: string; readonly code: string; readonly name: string }
  | { readonly kind: 'column'; readonly tableId: number; readonly code: string; readonly name: string }
  | { readonly kind: 'row'; readonly tableId: number; readonly rowKey: string; readonly name: string }
  | { readonly kind: 'rule'; readonly tableId: number; readonly code: string };

interface ManageSeed {
  readonly ruleId: number | null;
  readonly draft: UpdatePeriodAccessRuleDraft;
}

function emptyManageSeed(templateVersionId: number): ManageSeed {
  return {
    ruleId: null,
    draft: updatePeriodAccessRuleDraftOf({
      id: 0,
      templateVersionId,
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
  };
}

/**
 * Редактор структури версії.
 *
 * ⛔ Чернетки діалогів — НЕ стан цієї сторінки (`LocalDraft`): тут лише
 * «що відкрито і з чого почати». Інакше кожне натискання клавіші в діалозі
 * перерендерювало б усе дерево структури — на версії з 91 таблицею це
 * секунди на символ (замір 2026-09-23).
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
  const [publishing, setPublishing] = useState(false);
  const [deprecating, setDeprecating] = useState(false);
  const [editing, setEditing] = useState<TemplateColumnDto | null>(null);

  // ⚠ «Презентаційний редактор уже відкривали». Назад у `false` не вертається
  // навмисно — див. коментар біля `PresentationEditor` вище.
  const [presentationUsed, setPresentationUsed] = useState(false);
  const [sheetDraft, setSheetDraft] = useState<SheetDraft | null>(null);
  /**
   * Розгорнуті аркуші — лише для `LazyTableSlots`: перша таблиця розгорнутого
   * аркуша монтується без події видимості. `Accordion` лишається
   * некерованим — стан лише читається з `onChange`.
   */
  const [openSheets, setOpenSheets] = useState<string[]>([]);
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
  // ⛔ X-02: правка НАЯВНОЇ колонки несе лише адресу (`draft: null`) — форма
  // чекає повну відповідь `GET …/columns/{code}` (`ExistingColumn`), а не
  // стартує з бідного опису структури, як доти.
  const [columnEdit, setColumnEdit] = useState<
    { tableId: number; code: string; draft: ColumnDraft | null } | null
  >(null);
  const [pendingDelete, setPendingDelete] = useState<DeleteTarget | null>(null);
  const [rowEdit, setRowEdit] = useState<{ tableId: number; draft: RowDraft } | null>(null);

  // ⚠ Поле шапки документа (рівень усього документа, не таблиці) не
  // адресується `tableId` — на відміну від чернеток колонки й рядка вище.
  const [headerFieldEdit, setHeaderFieldEdit] = useState<HeaderFieldDraft | null>(null);

  // ФВ-8.14: id колонки, для якої зараз відкрито «де використовується».
  // ⛔ Кнопка показується для КОЖНОЇ колонки завжди, незалежно від того, чи є
  // в неї використання (`total: 0` теж чинний, а не привід ховати афордансу).
  const [columnUsageFor, setColumnUsageFor] = useState<number | null>(null);

  // ⛔ Кеш повних відповідей ЦЬОГО сеансу, ключ — `tableId:код`. Структура
  // версії (`GET …/structure`) віддає рядок бідніше, ніж його приймає й
  // повертає `PUT` (`Q-012`, докладніше в `row.ts`): без цього кешу повторне
  // відкриття форми правки губило б переклади підпису рядка. Колонці такий
  // кеш більше не потрібен — її форма читає `GET …/columns/{code}` (X-02).
  const [savedRows, setSavedRows] = useState<Record<string, RowDefDto>>({});

  // ⚠ Правило валідації (W5.4) адресується ТАБЛИЦЕЮ: чернетка тримається
  // разом із таблицею, для якої відкрили форму — той самий tableId їде і в
  // PUT, і в DELETE.
  const [validationRuleTable, setValidationRuleTable] = useState<number | null>(null);

  // ⛔ Правила доступу до періоду (`ФВ-2.15`) не мають коду — форма
  // створення і форма правки наявного за `id` навмисно окремі, за тією самою
  // причиною, що описана в `PeriodAccessRuleEditor.tsx`. Самі чернетки живуть
  // у діалозі (`LocalDraft`); тут — лише ключі, якими сторінка підставляє
  // нове початкове значення.
  const [periodRulesOpen, setPeriodRulesOpen] = useState(false);
  const [periodCreateKey, setPeriodCreateKey] = useState(0);
  const [manageSeed, setManageSeed] = useState<{ key: number; value: ManageSeed }>(() => ({
    key: 0,
    value: emptyManageSeed(id),
  }));

  const structure = useQuery({
    queryKey: queryKeys.templates.version(id),
    queryFn: () => apiFetch<TemplateStructureDto>(`/api/v1/template-versions/${id}/structure`),
  });

  /**
   * Поля шапки документа версії (рівень усього документа, не таблиці) —
   * окремий ендпоінт від `…/structure` (контракт серверної сесії): відповідь
   * несе повний `HeaderFieldDefDto` без бідної структури-проти-PUT, яку
   * компенсує `savedColumns` для колонок (`headerField.ts`).
   */
  const headerFields = useQuery({
    queryKey: queryKeys.templates.headerFieldsOf(id),
    queryFn: () => getHeaderFields(id),
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
      // ⛔ X-33: стан версії (а з ним «Publish» / «Withdraw from use») сторінка
      // читає з ПЕРЕЛІКУ версій шаблону — без його інвалідації після публікації
      // лишалася кнопка «Publish», а «Withdraw» з'являлася лише після reload.
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.templates.version(id) }),
        queryClient.invalidateQueries({ queryKey: queryKeys.templates.allVersionsOf() }),
      ]);
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
    mutationFn: (newVersion: string) =>
      apiFetch<VersionIdResponse>(`/api/v1/template-versions/${id}/clone`, {
        method: 'POST',
        body: JSON.stringify({ newVersion: newVersion.trim() } satisfies CloneVersionRequest),
      }),
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.templates.allVersionsOf() });
      setCloning(false);
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
      setPendingDelete(null);
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
      setPendingDelete(null);
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
    onSuccess: async () => {
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
      setPendingDelete(null);
      showDone(t('columns.deleted'));
    },
    onError: showApiError,
  });

  /**
   * Запис поля шапки документа — той самий draft→publish контракт, що
   * колонка (`W5.2`), рівень усього документа. DELETE не існує (свідоме
   * рішення серверної сесії, `docs/build/02-contracts.md` §9) — тому, на
   * відміну від колонки, для полів шапки нижче немає мутації видалення.
   */
  const saveHeaderFieldMutation = useMutation({
    mutationFn: (draft: HeaderFieldDraft) => saveHeaderField(id, draft),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.templates.headerFieldsOf(id) });
      setHeaderFieldEdit(null);
      showDone(t('headerFields.saved'));
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
      setPendingDelete(null);
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
    onSuccess: async (_result, variables) => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.templates.version(id) }),
        queryClient.invalidateQueries({ queryKey: validationRulesKey(id, variables.tableId) }),
      ]);
      setValidationRuleTable(null);
      showDone(t('validationRules.saved'));
    },
    onError: showApiError,
  });

  /** Видалення правила валідації — фізичне (на відміну від аркуша). */
  const deleteValidationRuleMutation = useMutation({
    mutationFn: ({ tableId, code }: { tableId: number; code: string }) =>
      deleteValidationRule(id, tableId, code),
    onSuccess: async (_result, variables) => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.templates.version(id) }),
        queryClient.invalidateQueries({ queryKey: validationRulesKey(id, variables.tableId) }),
      ]);
      setPendingDelete(null);
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
      setPeriodCreateKey((n) => n + 1);

      // ⚠ Щойно створене правило одразу підставляється у форму «правка за
      // id» нижче: інакше побачений на екрані id довелося б передруковувати
      // руками, щоб одразу ж його відредагувати чи прибрати.
      setManageSeed((seed) => ({
        key: seed.key + 1,
        value: { ruleId: created.id, draft: updatePeriodAccessRuleDraftOf(created) },
      }));
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
      setManageSeed((seed) => ({ key: seed.key + 1, value: emptyManageSeed(id) }));
      showDone(t('periodRules.deleted'));
    },
    onError: showApiError,
  });
  // ⛔ UI-аудит, lane 7: `editable` тут БУВ зашитий у `true` — коментар-
  // попередник пояснював це тим, що `TemplateStructureDto` не несе статусу
  // версії, а перелік версій живе на ІНШІЙ сторінці. Наслідок — «Publish» і
  // «Withdraw from use» лишалися повністю активними НАЗАВЖДИ, включно з уже
  // опублікованою чи виведеною з обігу версією, хоча сервер (`ECR-TMPL-0409`)
  // однаково відхилив би обидві дії поза їхнім єдиним допустимим станом
  // (`TemplateVersion.cs`: `Publish` вимагає `Draft`, `Deprecate` — саме
  // `Published`, ніколи не `Draft`). Перелік версій — той самий ендпоінт,
  // що вже working на `ExpressionsPage`/`TemplateVersionsPage` — дає статус
  // без потреби розширювати контракт `TemplateStructureDto` заради одного
  // поля.
  const templateIdNumber = Number(templateId);

  const versionsList = useQuery({
    queryKey: queryKeys.templates.versionsOf(templateIdNumber),
    queryFn: () =>
      apiFetch<TemplateVersionPageDto>(`/api/v1/templates/${templateIdNumber}/versions?limit=100`),
    enabled: Number.isFinite(templateIdNumber),
  });

  const versionSummary = versionsList.data?.items.find((v) => v.id === id);
  const versionStatus = versionSummary?.status;

  /*
   * ⛔ X-15: ролі правила доступу до періоду вибираються зі списку, а не
   * вводяться ідентифікатором. Перелік ролей — право `Security.ManageRoles`;
   * без нього (чи при відмові) форма лишає числове поле (`roles: null`).
   */
  const canListRoles = can(session.data, 'Security.ManageRoles');
  const roles = useQuery({
    queryKey: ['roles'],
    queryFn: () => apiFetch<RoleView[]>('/api/v1/roles'),
    enabled: periodRulesOpen && canListRoles,
  });
  const roleOptions =
    !canListRoles || roles.error !== null
      ? null
      : (roles.data ?? []).map((role) => ({ id: role.id, label: role.code }));
  const canPublish = versionStatus === 'Draft';
  const canWithdraw = versionStatus === 'Published';

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
        // ⛔ X-17: тут стояв ІДЕНТИФІКАТОР версії («Template version 10»), а не її
        // номер — людина шукала версію «10», якої в шаблоні немає.
        title={
          versionSummary === undefined
            ? t('version.title')
            : t('version.titleOf', { version: versionSummary.version })
        }
        actions={
          <Group gap="xs">
            {/* ⚠ Лічильник правок презентаційного шару (кнопка «Appearance»
                на колонці: підпис, порядок, формат, видимість — `ФВ-7.2`).
                Раніше тут стояло голе `r0`, і людина не розуміла, що це. */}
            {structure.data !== undefined && (
              <Text size="xs" c="dimmed" data-presentation-revision>
                {t('version.presentationRevision', {
                  revision: structure.data.presentationRevision,
                })}
              </Text>
            )}
            {/* ⚠ Порівняння версій доступне за правом ПЕРЕГЛЯДУ: питання
                «що зміниться» законне й для того, хто нічого не править —
                саме з нього починається рішення про міграцію. */}
            {can(session.data, 'Template.View') && <VersionDiff templateVersionId={id} versions={versionsList.data?.items} />}

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

            {canPublish && can(session.data, 'Template.Publish') && (
              <Button size="xs" onClick={() => setPublishing(true)}>
                {t('version.publish')}
              </Button>
            )}

            {/* ⚠ Право те саме, що на публікацію: вивести з обігу —
                рішення тієї самої ваги, що й випустити. Показано лише для
                вже ОПУБЛІКОВАНОЇ версії — сервер (`ECR-TMPL-0409`) відмовляє
                чернетці й уже виведеній з обігу версії однаково. */}
            {canWithdraw && can(session.data, 'Template.Publish') && (
              <Button
                size="xs"
                variant="default"
                color="statusError"
                onClick={() => setDeprecating(true)}
              >
                {t('version.deprecate')}
              </Button>
            )}
          </Group>
        }
      />

      {/*
       * ⛔ Аудит-пас 8, lane7, п.11: опублікована версія коректно не дає
       * редагувати структуру, але ЄДИНЕ пояснення до фіксу жило ВСЕРЕДИНІ
       * діалогу клонування (`version.cloneHint`), який треба здогадатися
       * відкрити. Мітка `· Fixed` біля таблиці (`tableDef.rowModeFixed`) —
       * про ІНШЕ (спосіб формування рядків, не стан заморозки) і оманливо
       * виглядає як пояснення. Банер тут — НА самій сторінці, а не лише в
       * діалозі клонування, показується за тим самим прапорцем, що вже
       * ховає форму аркуша (`canEditSheets` вище читає той самий
       * `structure.data?.isEditable`).
       */}
      {/*
        ⛔ Директива D15 §0, правило L10: стан версії рахується як
        `versionsList.data?.items.find(…)?.status`, тож при відмові
        `GET /templates/{id}/versions` він `undefined` — рівно те саме
        значення, що й «версії немає в переліку». Обидві кнопки нижче
        (`canPublish`, `canWithdraw`) через це ЗНИКАЛИ, і адміністратор із
        правом `Template.Publish` читав це як «версію вже опубліковано» або
        «права немає» — і йшов шукати іншу версію чи іншу людину.

        ⚠ Самі кнопки лишаються схованими: коли стан невідомий, дія має
        деградувати в бік ЗАБОРОНИ (той самий висновок, що в `PeriodsPage`,
        #452). Нове тут — видима причина замість мовчання.

        ⚠ Банер лише тому, хто має право публікувати: решта цих кнопок не
        бачить ніколи, і повідомлення про перелік, яким вони не
        користуються, було б шумом.
      */}
      {can(session.data, 'Template.Publish') && versionsList.error !== null && (
        <ErrorAlert error={versionsList.error} onRetry={() => void versionsList.refetch()} />
      )}

      {can(session.data, 'Template.Edit') && structure.data?.isEditable === false && (
        <Alert color="statusWarning" variant="light" mb="sm">
          {t('version.structureFrozen')}
        </Alert>
      )}

      {/*
       * ⛔ Поля шапки документа (рівень усього документа, не таблиці) — той
       * самий draft→publish контракт, що колонки (`W5.2`), тому та сама
       * заморожена структура (банер вище, `canEditSheets` — той самий прапорець
       * `structure.data?.isEditable`) забороняє й тут: другого банера немає
       * навмисно, причина заморозки на сторінці вже одна.
       */}
      <Group justify="space-between" mb="xs" mt="md">
        <Text fw={600}>{t('headerFields.title')}</Text>
        {canEditSheets && (
          <Button
            size="xs"
            variant="default"
            onClick={() => setHeaderFieldEdit(emptyHeaderFieldDraft(null))}
          >
            {t('headerFields.add')}
          </Button>
        )}
      </Group>

      {headerFields.error !== null && (
        <ErrorAlert error={headerFields.error} onRetry={() => void headerFields.refetch()} />
      )}

      {headerFields.error === null && headerFields.isPending && (
        <Skeleton height={80} radius="sm" mb="sm" />
      )}

      {headerFields.error === null && !headerFields.isPending && (
        headerFields.data.length === 0 ? (
          <Text size="sm" c="dimmed" mb="sm">
            {t('headerFields.empty')}
          </Text>
        ) : (
          <Table striped withTableBorder mb="md">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('headerFields.label')}</Table.Th>
                <Table.Th>{t('headerFields.dataType')}</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {[...headerFields.data]
                .sort((a, b) => a.ordinal - b.ordinal)
                .map((field) => (
                  <Table.Tr key={field.id}>
                    <Table.Td>
                      {localized(field.labelL10n) || field.code}{' '}
                      <Text span c="dimmed">
                        ({field.code})
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      {dataTypeLabel(field.dataType)}
                      {field.isRequired && (
                        <Badge ml="xs" size="xs" variant="light">
                          {t('headerFields.required')}
                        </Badge>
                      )}
                    </Table.Td>
                    <Table.Td>
                      {canEditSheets && (
                        <Group gap="xs" wrap="nowrap" justify="flex-end">
                          <Button
                            size="compact-xs"
                            variant="subtle"
                            onClick={() => setHeaderFieldEdit(headerFieldDraftOf(field))}
                          >
                            {t('headerFields.edit')}
                          </Button>
                        </Group>
                      )}
                    </Table.Td>
                  </Table.Tr>
                ))}
            </Table.Tbody>
          </Table>
        )
      )}

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

            <Accordion multiple onChange={setOpenSheets}>
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
                            // ⛔ Лише натиснута кнопка: доти `loading` крутився
                            // на кнопках УСІХ аркушів одночасно.
                            loading={deleteSheetMutation.isPending && deleteSheetMutation.variables === sheet.code}
                            onClick={() =>
                              setPendingDelete({
                                kind: 'sheet',
                                code: sheet.code,
                                name: localized(sheet.nameL10n) || sheet.code,
                              })
                            }
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

                      <LazyTableSlots
                        items={sheet.tables}
                        active={openSheets.includes(sheet.code)}
                        nameOf={(table) => localized(table.nameL10n) || table.code}
                        titleOf={(table) => <TableTitle table={table} />}
                        heightOf={estimateTemplateTableHeight}
                        render={(table) => {
                        const nextColumnOrdinal =
                          table.columns.length === 0
                            ? 0
                            : Math.max(...table.columns.map((c) => c.ordinal)) + 1;
                        const nextRowOrdinal =
                          table.rows.length === 0 ? 0 : Math.max(...table.rows.map((r) => r.ordinal)) + 1;

                        return (
                          <>
                            <Group justify="space-between" mt="sm">
                              <Text fw={600}>
                                <TableTitle table={table} />
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
                                      setPendingDelete({
                                        kind: 'table',
                                        sheetCode: sheet.code,
                                        code: table.code,
                                        name: localized(table.nameL10n) || table.code,
                                      })
                                    }
                                  >
                                    {t('tableDef.delete')}
                                  </Button>
                                  <Button
                                    size="compact-xs"
                                    variant="default"
                                    onClick={() =>
                                      setColumnEdit({
                                        tableId: table.id,
                                        code: '',
                                        draft: emptyColumnDraft(nextColumnOrdinal),
                                      })
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
                                    onClick={() => setValidationRuleTable(table.id)}
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
                                      {dataTypeLabel(column.dataType)}
                                      {column.isReadOnly && (
                                        <Badge ml="xs" size="xs" variant="light">
                                          {t('version.readOnly')}
                                        </Badge>
                                      )}
                                    </Table.Td>
                                    <Table.Td>{column.unitSymbol ?? '—'}</Table.Td>
                                    <Table.Td>
                                      <Group gap="xs" wrap="nowrap" justify="flex-end">
                                        <Button
                                          size="compact-xs"
                                          variant="subtle"
                                          data-column-usage="trigger"
                                          onClick={() => setColumnUsageFor(column.id)}
                                        >
                                          {t('registries.tabUsage')}
                                        </Button>
                                        {/* ⚠ Правка тут не потребує нової версії: підпис,
                                            порядок, формат і видимість — презентаційний
                                            шар, і його дозволено міняти в опублікованій
                                            версії (`ФВ-7.2`). */}
                                        {can(session.data, 'Template.Edit') && (
                                          <Button
                                            size="compact-xs"
                                            variant="subtle"
                                            onClick={() => {
                                              setPresentationUsed(true);
                                              setEditing(column);
                                            }}
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
                                                setColumnEdit({ tableId: table.id, code: column.code, draft: null })
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
                                                setPendingDelete({
                                                  kind: 'column',
                                                  tableId: table.id,
                                                  code: column.code,
                                                  name: localized(column.headerL10n) || column.code,
                                                })
                                              }
                                            >
                                              {t('columns.delete')}
                                            </Button>
                                            <Button
                                              size="compact-xs"
                                              variant="subtle"
                                              onClick={() =>
                                                setFormulaDraft(
                                                  // ⛔ Дефект 2026-09-23: раніше тут завжди був
                                                  // emptyFormulaDraft — повторне відкриття на
                                                  // колонці зі збереженою формулою показувало
                                                  // порожній редактор, хоча PUT зберігав вираз
                                                  // (структура тепер несе його — GET …/structure).
                                                  column.formulaExpression !== null
                                                    ? draftOfFormula(table.id, 'Column', String(column.id), {
                                                        dialect: column.formulaDialect ?? 'Template',
                                                        expression: column.formulaExpression,
                                                      })
                                                    : emptyFormulaDraft(table.id, 'Column', String(column.id)),
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
                                          <Table.Td>{rowKindLabel(row.rowKind)}</Table.Td>
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
                                                    setPendingDelete({
                                                      kind: 'row',
                                                      tableId: table.id,
                                                      rowKey: row.rowKey,
                                                      name: row.label ?? row.rowKey,
                                                    })
                                                  }
                                                >
                                                  {t('rows.delete')}
                                                </Button>
                                                <Button
                                                  size="compact-xs"
                                                  variant="subtle"
                                                  onClick={() =>
                                                    setFormulaDraft(
                                                      // ⛔ Той самий фікс, що й на колонці вище
                                                      // (2026-09-23): порожній редактор на
                                                      // рядку зі збереженою формулою.
                                                      row.formulaExpression !== null
                                                        ? draftOfFormula(table.id, 'Row', row.rowKey, {
                                                            dialect: row.formulaDialect ?? 'Template',
                                                            expression: row.formulaExpression,
                                                          })
                                                        : emptyFormulaDraft(table.id, 'Row', row.rowKey),
                                                    )
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
                          </>
                        );
                        }}
                      />
                    </Accordion.Panel>
                  </Accordion.Item>
                ))}
            </Accordion>
          </>
        )}
      </AsyncBoundary>

      {presentationUsed && (
        <Suspense fallback={null}>
          <PresentationEditor
            templateVersionId={id}
            column={editing}
            onClose={() => setEditing(null)}
          />
        </Suspense>
      )}

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
        <LocalDraft initial="">
          {(newVersion, setNewVersion) => (
            <>
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
                  onClick={() => clone.mutate(newVersion)}
                >
                  {t('version.clone')}
                </Button>
              </Group>
            </>
          )}
        </LocalDraft>
      </Modal>

      <Modal
        opened={sheetDraft !== null}
        onClose={() => setSheetDraft(null)}
        title={sheetDraft?.isNew === true ? t('sheets.add') : t('sheets.edit')}
      >
        {sheetDraft !== null && (
          <LocalDraft initial={sheetDraft}>
            {(draft, setDraft) => (
              <Suspense fallback={null}>
                <SheetEditor
                  draft={draft}
                  disabled={!canEditSheets}
                  saving={saveSheetMutation.isPending}
                  onChange={setDraft}
                  onSubmit={() => saveSheetMutation.mutate(draft)}
                  onCancel={() => setSheetDraft(null)}
                />
              </Suspense>
            )}
          </LocalDraft>
        )}
      </Modal>

      <Modal
        opened={tableDraft !== null}
        onClose={() => setTableDraft(null)}
        title={tableDraft?.draft.isNew === true ? t('tableDef.add') : t('tableDef.edit')}
      >
        {tableDraft !== null && (
          <LocalDraft initial={tableDraft.draft}>
            {(draft, setDraft) => (
              <Suspense fallback={null}>
                <TableEditor
                  draft={draft}
                  disabled={!canEditSheets}
                  saving={saveTableMutation.isPending}
                  onChange={setDraft}
                  onSubmit={() => saveTableMutation.mutate({ sheetCode: tableDraft.sheetCode, draft })}
                  onCancel={() => setTableDraft(null)}
                />
              </Suspense>
            )}
          </LocalDraft>
        )}
      </Modal>

      <Modal
        opened={columnEdit !== null}
        onClose={() => setColumnEdit(null)}
        title={columnEdit?.draft?.isNew === true ? t('columns.add') : t('columns.edit')}
      >
        {columnEdit !== null && (() => {
          const editor = (initial: ColumnDraft): JSX.Element => (
            <LocalDraft initial={initial}>
              {(draft, setDraft) => (
                <Suspense fallback={null}>
                  <ColumnEditor
                    draft={draft}
                    disabled={!canEditSheets}
                    saving={saveColumnMutation.isPending}
                    templateVersionId={id}
                    onChange={setDraft}
                    onSubmit={() => saveColumnMutation.mutate({ tableId: columnEdit.tableId, draft })}
                    onCancel={() => setColumnEdit(null)}
                  />
                </Suspense>
              )}
            </LocalDraft>
          );

          return columnEdit.draft !== null ? (
            editor(columnEdit.draft)
          ) : (
            <ExistingColumn templateVersionId={id} tableId={columnEdit.tableId} code={columnEdit.code}>
              {editor}
            </ExistingColumn>
          );
        })()}
      </Modal>

      <Modal
        opened={headerFieldEdit !== null}
        onClose={() => setHeaderFieldEdit(null)}
        title={headerFieldEdit?.isNew === true ? t('headerFields.add') : t('headerFields.edit')}
      >
        {headerFieldEdit !== null && (
          <LocalDraft initial={headerFieldEdit}>
            {(draft, setDraft) => (
              <Suspense fallback={null}>
                <HeaderFieldEditor
                  draft={draft}
                  disabled={!canEditSheets}
                  saving={saveHeaderFieldMutation.isPending}
                  onChange={setDraft}
                  onSubmit={() => saveHeaderFieldMutation.mutate(draft)}
                  onCancel={() => setHeaderFieldEdit(null)}
                />
              </Suspense>
            )}
          </LocalDraft>
        )}
      </Modal>

      <Modal
        opened={columnUsageFor !== null}
        onClose={() => setColumnUsageFor(null)}
        title={t('registries.tabUsage')}
      >
        {columnUsageFor !== null && (
          <Suspense fallback={null}>
            <TemplateColumnUsage columnDefId={columnUsageFor} isDraft={versionStatus === 'Draft'} />
          </Suspense>
        )}
      </Modal>

      <Modal
        opened={rowEdit !== null}
        onClose={() => setRowEdit(null)}
        title={rowEdit?.draft.isNew === true ? t('rows.add') : t('rows.edit')}
      >
        {rowEdit !== null && (
          <LocalDraft initial={rowEdit.draft}>
            {(draft, setDraft) => (
              <Suspense fallback={null}>
                <RowEditor
                  draft={draft}
                  disabled={!canEditSheets}
                  saving={saveRowMutation.isPending}
                  onChange={setDraft}
                  onSubmit={() => saveRowMutation.mutate({ tableId: rowEdit.tableId, draft })}
                  onCancel={() => setRowEdit(null)}
                />
              </Suspense>
            )}
          </LocalDraft>
        )}
      </Modal>

      <Modal
        opened={formulaDraft !== null}
        onClose={() => setFormulaDraft(null)}
        title={t('formulas.edit')}
        size="lg"
      >
        {formulaDraft !== null && (
          <LocalDraft initial={formulaDraft}>
            {(draft, setDraft) => (
              <Suspense fallback={null}>
                <FormulaEditor
                  draft={draft}
                  templateVersionId={id}
                  structure={structure.data}
                  disabled={!canEditSheets}
                  saving={saveFormulaMutation.isPending}
                  onChange={setDraft}
                  onSubmit={() => saveFormulaMutation.mutate(draft)}
                  onCancel={() => setFormulaDraft(null)}
                />
              </Suspense>
            )}
          </LocalDraft>
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
            {/* ⚠ Межа охоплює ЛИШЕ форму, а не весь `Stack`: видалення за кодом
                нижче — звичайні `TextInput`+`Button` із цього ж файла, і
                ховати їх на час завантаження чужого чанка немає підстав. */}
            <LocalDraft initial={emptyValidationRuleDraft()}>
              {(draft, setDraft) => (
                <Suspense fallback={null}>
                  <ValidationRuleEditor
                    draft={draft}
                    templateVersionId={id}
                    tableDefId={validationRuleTable}
                    structure={structure.data}
                    disabled={!canEditSheets}
                    saving={saveValidationRuleMutation.isPending}
                    onChange={setDraft}
                    onSubmit={() =>
                      saveValidationRuleMutation.mutate({ tableId: validationRuleTable, draft })
                    }
                    onCancel={() => setValidationRuleTable(null)}
                  />
                </Suspense>
              )}
            </LocalDraft>

            <Divider label={t('validationRules.existing')} />

            {/* ⛔ X-15: наявні правила ПЕРЕЛІКОМ із видаленням вибраного, а не
                текстове поле коду, який треба було пам'ятати. */}
            <Suspense fallback={null}>
              <ValidationRuleList
                templateVersionId={id}
                tableDefId={validationRuleTable}
                disabled={!canEditSheets}
                deletingCode={
                  deleteValidationRuleMutation.isPending
                    ? (deleteValidationRuleMutation.variables?.code ?? null)
                    : null
                }
                onDelete={(code) => setPendingDelete({ kind: 'rule', tableId: validationRuleTable, code })}
              />
            </Suspense>
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
        {/* ⚠ Одна межа на обидві форми, а не дві: вони живуть в одному модулі,
            тобто в одному чанку — друга межа дала б другий порожній кадр без
            жодної користі. */}
        {/* ⚠ Обидві чернетки — локальні (`LocalDraft`), як і решта діалогів:
            сторінка лише підставляє нове початкове значення через `key`
            (щойно створене правило, скидання після видалення). Недозбережене
            введення при закритті вікна тепер не зберігається — як у кожному
            іншому діалозі цієї сторінки. */}
        <Suspense fallback={null}>
        <Stack gap="lg">
          {/* ⛔ R-21: обидві `LocalDraft` — сиблінги з числовим `key` від нуля,
              тобто ОДНАКОВИМ ключем «0» (React: «Encountered two children with
              the same key»). Префікс розводить їх. */}
          <LocalDraft key={`create-${String(periodCreateKey)}`} initial={emptyPeriodAccessRuleDraft()}>
            {(draft, setDraft) => (
              <PeriodAccessRuleEditor
                draft={draft}
                structure={structure.data}
                roles={roleOptions}
                disabled={!canEditSheets}
                saving={createPeriodRuleMutation.isPending}
                onChange={setDraft}
                onSubmit={() => createPeriodRuleMutation.mutate(draft)}
              />
            )}
          </LocalDraft>

          <Divider label={t('periodRules.manage')} />

          <LocalDraft key={`manage-${String(manageSeed.key)}`} initial={manageSeed.value}>
            {(manage, setManage) => (
              <PeriodAccessRuleManager
                ruleId={manage.ruleId}
                draft={manage.draft}
                structure={structure.data}
                roles={roleOptions}
                disabled={!canEditSheets}
                saving={savePeriodRuleMutation.isPending}
                deleting={deletePeriodRuleMutation.isPending}
                onRuleIdChange={(ruleId) => setManage({ ...manage, ruleId })}
                onChange={(draft) => setManage({ ...manage, draft })}
                onSave={() => {
                  if (manage.ruleId !== null) {
                    savePeriodRuleMutation.mutate({ ruleId: manage.ruleId, draft: manage.draft });
                  }
                }}
                onDelete={() => {
                  if (manage.ruleId !== null) {
                    deletePeriodRuleMutation.mutate(manage.ruleId);
                  }
                }}
              />
            )}
          </LocalDraft>
        </Stack>
        </Suspense>
      </Modal>

      <ConfirmModal
        opened={pendingDelete !== null}
        title={deleteTitle(pendingDelete)}
        text={pendingDelete?.kind === 'rule' ? t('validationRules.deleteText') : t('structure.deleteText')}
        verb={deleteVerb(pendingDelete)}
        isPending={
          deleteSheetMutation.isPending ||
          deleteTableMutation.isPending ||
          deleteColumnMutation.isPending ||
          deleteRowMutation.isPending ||
          deleteValidationRuleMutation.isPending
        }
        onConfirm={() => {
          if (pendingDelete === null) return;

          switch (pendingDelete.kind) {
            case 'sheet':
              deleteSheetMutation.mutate(pendingDelete.code);
              break;
            case 'table':
              deleteTableMutation.mutate({ sheetCode: pendingDelete.sheetCode, code: pendingDelete.code });
              break;
            case 'column':
              deleteColumnMutation.mutate({ tableId: pendingDelete.tableId, code: pendingDelete.code });
              break;
            case 'row':
              deleteRowMutation.mutate({ tableId: pendingDelete.tableId, rowKey: pendingDelete.rowKey });
              break;
            case 'rule':
              deleteValidationRuleMutation.mutate({ tableId: pendingDelete.tableId, code: pendingDelete.code });
              break;
          }
        }}
        onClose={() => setPendingDelete(null)}
      />
    </>
  );
}

/** Заголовок підтвердження — з назвою об'єкта (`ConfirmModal`, правило `L6`). */
function deleteTitle(target: DeleteTarget | null): string {
  switch (target?.kind) {
    case 'sheet':
      return t('sheets.deleteTitle', { name: target.name });
    case 'table':
      return t('tableDef.deleteTitle', { name: target.name });
    case 'column':
      return t('columns.deleteTitle', { name: target.name });
    case 'row':
      return t('rows.deleteTitle', { name: target.name });
    case 'rule':
      return t('validationRules.deleteTitle', { code: target.code });
    default:
      return '';
  }
}

/** Дієслово кнопки підтвердження — той самий підпис, що на кнопці в рядку. */
function deleteVerb(target: DeleteTarget | null): string {
  switch (target?.kind) {
    case 'sheet':
      return t('sheets.delete');
    case 'table':
      return t('tableDef.delete');
    case 'column':
      return t('columns.delete');
    case 'row':
      return t('rows.delete');
    case 'rule':
      return t('validationRules.delete');
    default:
      return t('common.delete');
  }
}
