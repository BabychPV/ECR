import { memo, useRef, type JSX } from 'react';
import {
  Badge,
  Button,
  Checkbox,
  Group,
  NumberInput,
  SegmentedControl,
  Select,
  Table,
  Text,
  TextInput,
} from '@mantine/core';
import type { CellChangePage } from '@/api/types';
import { authorName, useAuthorOptions } from '@/features/audit/authorOptions';
import { cellChangeOrigins, cellChangesQuery, isSingleCell, useCellChanges } from '@/features/audit/api';
import { FilterHints, readerOnlyDescription } from '@/features/audit/FilterHints';
import { StructureChangesPanel } from '@/features/audit/StructureChangesPanel';
import { StructureExportButton } from '@/features/audit/StructureExportButton';
import { Timestamp } from '@/shared/ui/Timestamp';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { useDebouncedFilter, useFilterCursor } from '@/shared/ui/useDebouncedFilter';
import { useFieldDraft } from '@/shared/ui/useFieldDraft';
import { useUrlNumber, useUrlParamsSetter, useUrlState } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { formatDate, formatDecimal } from '@/shared/format';

/**
 * Журнал змін комірок (`ФВ-6.13`).
 *
 * ⛔ Екрана не було: право `Security.ViewAudit` видавалося ролям, ендпоінт
 * працював — а подивитися журнал через інтерфейс було неможливо. Тобто
 * відповідь на питання «хто змінив це число» існувала і була недосяжна, а це
 * головне питання, заради якого аудит взагалі ведуть.
 *
 * ⛔ Вікно часу **обов'язкове**. Таблиця партиційована за `ChangedAt`, і запит
 * без вікна пішов би по всіх партиціях — на журналі за роки це не «повільно»,
 * а «сервер зайнятий». Тому поля дат заповнені за замовчуванням останнім
 * тижнем, а не порожні.
 *
 * ⚠ Журнал **тільки читається**: ані правки, ані видалення тут немає і не
 * буде. Журнал, який можна відредагувати, не є доказом.
 *
 * ⛔ `BE-03`: фільтри `author`/`origin`/`lateOnly` і адреса комірки
 * (`rowKey` + `columnDefId`). До цього журнал умів лише «документ за вікном»,
 * тобто на питання «хто змінив ЦЕ число» доводилося гортати тисячі рядків —
 * а це і є головне питання, заради якого аудит ведуть.
 *
 * ⚠ Документ + рядок + колонка разом — це історія ОДНІЄЇ комірки, і сервер
 * приймає її за іншим правом (`Document.View` замість `Security.ViewAudit`) і
 * з ширшим вікном (13 місяців замість 92 днів, `D15-16`). Той самий екран, те
 * саме читання — інша межа доступу, і вона виражена САМИМ фільтром, а не
 * окремим маршрутом.
 */
export function AuditPage(): JSX.Element {
  const [from, setFrom] = useUrlState('from');
  const [to, setTo] = useUrlState('to');
  const [documentId, setDocumentId] = useUrlNumber('documentId');
  const [rowKey, setRowKey] = useUrlState('rowKey');
  const [columnDefId, setColumnDefId] = useUrlNumber('columnDefId');
  const [author, setAuthor] = useUrlNumber('author');
  const [origin, setOrigin] = useUrlState('origin');
  const [lateOnly, setLateOnly] = useUrlState('lateOnly');
  const [view, setView] = useUrlState('view');
  const setParams = useUrlParamsSetter();

  const fromDate = from ?? isoDaysAgo(7);
  const toDate = to ?? isoDaysAgo(0);

  /*
   * ⛔ Поля вводу показують ВЛАСНЕ значення (`useFieldDraft`), а не адресу:
   * адреса — похідна від поля й у нього не пише, поки воно у фокусі. Живий
   * стенд (2026-09-24, процесор 4×, 5 з 5): «Row key», кероване значенням з
   * адреси, при наборі `R12345` лишало `"R"` чи `"5"` — навігація
   * застосовується переходом, адреса запізнюється, і React повертав полю
   * старе значення між натисканнями. Зовнішня зміна (кнопка «Reset»,
   * навігація) приймається, коли поле не у фокусі.
   */
  const fromField = useFieldDraft(fromDate);
  const toField = useFieldDraft(toDate);
  const documentField = useFieldDraft<string | number>(documentId ?? '');
  const rowKeyField = useFieldDraft(rowKey ?? '');
  const columnField = useFieldDraft<string | number>(columnDefId ?? '');

  /*
   * ⛔ Поля, що набираються з клавіатури, йдуть у запит ПІСЛЯ паузи
   * (`useDebouncedFilter`): набір `R12345` у «Row key» давав шість запитів
   * по партиціонованому журналу замість одного. Поле й адреса — одразу.
   * Дати, «Origin» і «Late only» — дискретний вибір, їм чекати нічого.
   */
  const appliedDocumentId = useDebouncedFilter(documentId);
  const appliedRowKey = useDebouncedFilter(rowKey);
  const appliedColumnDefId = useDebouncedFilter(columnDefId);
  const appliedAuthor = useDebouncedFilter(author);

  /*
   * ⛔ «Row key»/«Column» без «Document» у запит НЕ йдуть: сервер на такий
   * запит гарантовано відповідає `422 ECR-REQ-0422` (ключ рядка унікальний
   * лише в межах документа). Поля лишаються активними — людина може набрати
   * їх раніше за документ, і значення з адреси не ховається, — а причину
   * показує виділене пояснення `audit.cellHint` під рядом фільтрів.
   */
  const cellNeedsDocument = appliedDocumentId === null;
  const cellWithoutDocument =
    documentId === null && ((rowKey !== null && rowKey.length > 0) || columnDefId !== null);

  const applied = {
    from: fromDate,
    to: toDate,
    documentId: appliedDocumentId,
    rowKey: cellNeedsDocument ? null : appliedRowKey,
    columnDefId: cellNeedsDocument ? null : appliedColumnDefId,
    author: appliedAuthor,
    origin,
    lateOnly: lateOnly === 'true',
    limit: 100,
  };

  // ⛔ Курсор скидається разом із ЗАСТОСОВАНИМ фільтром (`useFilterCursor`), а
  // не з `onChange` полів: інакше після «More» перша ж клавіша давала зайвий
  // запит «старий фільтр, перша сторінка» ще до паузи debounce.
  const [cursor, setCursor] = useFilterCursor(cellChangesQuery(applied));
  const filter = { ...applied, cursor };

  // ⚠ `BE-16`: друга вкладка — журнал структурних змін. Вкладка живе в адресі
  // (`?view=structure`), бо саме адресу людина надсилає колезі. Журнал комірок
  // на ній НЕ запитується: зайвий запит по партиціях заради невидимої таблиці.
  const structure = view === 'structure';
  const changes = useCellChanges(filter, !structure);

  // ⛔ `R-18`: попередня сторінка лишається на екрані, доки їде нова. Раніше кожен
  // застосований debounce фільтра клав скелет на місце таблиці й будував її
  // наново — саме ці кадри й давали затримку друку до 117 мс. Відмова й перше
  // завантаження показуються як і досі.
  const shownChanges = useLastData(changes.data, changes.error === null);

  /**
   * ⚠ Курсор скидається на КОЖНУ зміну фільтра — він позначає позицію в
   * конкретній видачі, і сторінка 5 попереднього фільтра не є сторінкою 5
   * нового. Без цього зміна фільтра давала б порожню сторінку замість перших
   * результатів, і це читалося б як «нічого не знайдено».
   */
  const single = isSingleCell(filter);

  const authorOptions = useAuthorOptions(changes.data?.items, author);

  return (
    <>
      <PageHeader
        title={t('audit.title')}
        actions={
          <Group gap="xs" align="end">
            <SegmentedControl
              size="xs"
              value={structure ? 'structure' : 'cells'}
              onChange={(value) => setView(value === 'structure' ? 'structure' : null)}
              data={[
                { value: 'cells', label: t('audit.viewCells') },
                { value: 'structure', label: t('audit.viewStructure') },
              ]}
            />
            <TextInput
              size="xs"
              // eslint-disable-next-line no-restricted-syntax -- D15-09, борг №1/8: перехід на DateInput змінює тип значення (string → Date) і стан сторінки, тому окремим PR; список боргу сторожить lintRules.test.ts
              type="date"
              label={t('audit.from')}
              value={fromField.value}
              onFocus={fromField.onFocus}
              onBlur={fromField.onBlur}
              onChange={(event) => {
                fromField.setValue(event.currentTarget.value);
                setFrom(event.currentTarget.value);
              }}
            />
            <TextInput
              size="xs"
              // eslint-disable-next-line no-restricted-syntax -- D15-09, борг №2/8: див. коментар вище
              type="date"
              label={t('audit.to')}
              value={toField.value}
              onFocus={toField.onFocus}
              onBlur={toField.onBlur}
              onChange={(event) => {
                toField.setValue(event.currentTarget.value);
                setTo(event.currentTarget.value);
              }}
            />
            <NumberInput
              size="xs"
              miw={140}
              // ⚠ На вкладці структурних змін документа немає: мертвий фільтр
              // читався б як «за цим документом змін не було».
              display={structure ? 'none' : undefined}
              label={t('audit.document')}
              // ⚠ `U-21`: пояснення лишається для читалки, а видиме — під рядом
              // фільтрів (`FilterHints`); інакше воно зсуває ряд шапки.
              description={t('audit.documentHint')}
              styles={readerOnlyDescription}
              value={documentField.value}
              onFocus={documentField.onFocus}
              onBlur={documentField.onBlur}
              onChange={(value) => {
                documentField.setValue(value);
                setDocumentId(typeof value === 'number' ? value : null);
              }}
            />
          </Group>
        }
      />

      {structure && <StructureExportButton from={fromDate} to={toDate} />}
      {structure && <StructureChangesPanel key={`${fromDate}:${toDate}`} from={fromDate} to={toDate} />}

      {/* ⚠ Відступ усередині цієї обгортки НАВМИСНО не зсунуто: зсув — це
          форматування 160 рядків, а воно йде окремим PR (CLAUDE.md, правило 4). */}
      {!structure && (
      <>
      {/* ⚠ Фільтри ОКРЕМИМ рядком, а не в шапці: їх шість, і в шапці вони
          витіснили б заголовок за край на ноутбучній ширині. */}
      <Group gap="xs" align="end" mb="xs" wrap="wrap" data-audit-filter-row="cells">
        {/* ⛔ `R-18`: автор — ВИБОРОМ за іменем (`useAuthorOptions`), а не
            номером `UserId`, якого людина не знає. */}
        <Select
          size="xs"
          miw={180}
          searchable
          clearable
          label={t('audit.author')}
          description={t('audit.authorHint')}
          styles={readerOnlyDescription}
          placeholder={t('audit.authorAny')}
          data={authorOptions}
          value={author === null ? null : String(author)}
          onChange={(value) => {
            setAuthor(value === null ? null : Number(value));
          }}
        />
        <Select
          size="xs"
          miw={160}
          clearable
          label={t('audit.origin')}
          placeholder={t('audit.originAny')}
          data={[...cellChangeOrigins]}
          value={origin}
          onChange={(value) => {
            setOrigin(value);
          }}
        />
        <TextInput
          size="xs"
          miw={120}
          label={t('audit.rowKey')}
          description={t('audit.cellHint')}
          styles={readerOnlyDescription}
          value={rowKeyField.value}
          onFocus={rowKeyField.onFocus}
          onBlur={rowKeyField.onBlur}
          onChange={(event) => {
            rowKeyField.setValue(event.currentTarget.value);
            setRowKey(event.currentTarget.value);
          }}
        />
        <NumberInput
          size="xs"
          miw={120}
          label={t('audit.columnDefId')}
          description={t('audit.cellHint')}
          styles={readerOnlyDescription}
          value={columnField.value}
          onFocus={columnField.onFocus}
          onBlur={columnField.onBlur}
          onChange={(value) => {
            columnField.setValue(value);
            setColumnDefId(typeof value === 'number' ? value : null);
          }}
        />
        <Checkbox
          mb="xs"
          label={t('audit.lateOnly')}
          checked={lateOnly === 'true'}
          onChange={(event) => {
            setLateOnly(event.currentTarget.checked ? 'true' : null);
          }}
        />
        {/* ⚠ Значок «історія однієї комірки» — не прикраса: саме в цьому стані
            сервер приймає запит за `Document.View` і з вікном у 13 місяців, а
            не за `Security.ViewAudit` і 92 дні. Без видимої ознаки людина не
            розуміє, чому те саме вікно то приймається, то ні. */}
        {single && (
          <Badge mb="xs" size="sm" variant="light" color="brand">
            {t('audit.cell')}
          </Badge>
        )}
        <Button
          size="xs"
          mb="xs"
          variant="subtle"
          onClick={() => {
            // ⛔ ОДИН перехід на п'ять параметрів, а не п'ять викликів
            // `useUrlState` підряд: два синхронні `setSearchParams` в одному
            // тіку гублять ОБИДВІ зміни, а не лише другу (див. коментар до
            // `useUrlParamsSetter`). П'ять поспіль не скинули б нічого.
            setParams({
              rowKey: null,
              columnDefId: null,
              author: null,
              origin: null,
              lateOnly: null,
            });
          }}
        >
          {t('audit.reset')}
        </Button>
      </Group>

      {/* ⚠ `U-21`: пояснення ПІД рядом, а не під підписами — інакше підписи
          полів із поясненням і без нього стоять на різній висоті. Пояснення
          до «Document» теж тут: поле живе в шапці поруч із датами, і його
          `description` зсував так само вже ряд шапки. */}
      <FilterHints
        texts={[t('audit.documentHint'), t('audit.authorHint'), t('audit.cellHint')]}
        active={cellWithoutDocument ? t('audit.cellHint') : null}
      />

      <AsyncBoundary<CellChangePage>
        isPending={changes.isPending && shownChanges === undefined}
        error={changes.error}
        data={shownChanges}
        isEmpty={(page) => page.items.length === 0}
        emptyTitle={t('audit.empty')}
        emptyHint={t('audit.emptyHint')}
        skeleton="table"
        onRetry={() => void changes.refetch()}
      >
        {(page) => (
          <>
            {/* ⛔ Окремий `memo`-компонент: набір у полях фільтра перемальовує
                сторінку на кожну клавішу, і 100 рядків таблиці разом із нею
                давали затримку друку до 117 мс (`R-18`). Сторінка відповіді
                від React Query стабільна за посиланням — таблиця малюється
                лише тоді, коли приходять нові дані. */}
            <CellChangesTable items={page.items} />

            {/* ⚠ Курсорна пагінація: журнал за рік — мільйони рядків, і
                `OFFSET` на сторінці 200 сканував би все, що до неї. */}
            {page.nextCursor !== null && (
              <Button mt="md" variant="default" onClick={() => setCursor(page.nextCursor)}>
                {t('documents.more')}
              </Button>
            )}
          </>
        )}
      </AsyncBoundary>
      </>
      )}
    </>
  );
}

type CellChange = CellChangePage['items'][number];

/** Останні отримані дані — поки нові ще в дорозі; відмова скидає запам'ятоване. */
function useLastData<T>(data: T | undefined, healthy: boolean): T | undefined {
  const last = useRef<T | undefined>(undefined);

  if (!healthy) last.current = undefined;
  else if (data !== undefined) last.current = data;

  return data ?? last.current;
}

/** Типи колонок, чиє значення — число (`U-05`: одне правило подачі числа). */
const NumericTypes: readonly string[] = ['Decimal', 'Int', 'Formula', 'Calculated'];

/**
 * Значення журналу — за правилом показу, а не у форматі сховища (`X-35`).
 *
 * ⛔ Журнал показував «Was 53.1771000000000000»: `aud.CellChange` зберігає
 * число з повним масштабом колонки сховища (`decimal(34,16)`, `D-148`).
 * Людина набирала `53.1771` і шукає в журналі саме його. Хвостові нулі
 * прибирає той самий `formatDecimal`, що й сітка (`U-05`/`U-24`), розряди —
 * мовою інтерфейсу.
 *
 * ⚠ Нерозібране значення показується ЯК Є: журнал — доказ, і сховати дивне
 * значення за «—» означало б сховати саму розбіжність.
 */
export function auditValueText(value: string | null | undefined, dataType: string | null | undefined): string {
  if (value === null || value === undefined) return '—';
  if (dataType === null || dataType === undefined) return value;

  if (NumericTypes.includes(dataType)) return formatDecimal(value) ?? value;

  if (dataType === 'Date') {
    const day = /^\d{4}-\d{2}-\d{2}/.exec(value)?.[0];
    const shown = day === undefined ? '' : formatDate(day);

    return shown.length > 0 ? shown : value;
  }

  return value;
}

/** Документ: людська назва → бізнес-ключ → номер (документа вже немає). */
function documentText(change: CellChange): string {
  const name = localized(change.documentNameL10n);

  if (name.length > 0) return name;
  if (change.documentBusinessKey !== null && change.documentBusinessKey !== undefined) {
    return change.documentBusinessKey;
  }

  return t('audit.documentGone', { id: change.documentId });
}

/** Колонка: заголовок мовою інтерфейсу, код — поруч; без запису — номер. */
function columnText(change: CellChange): string {
  const header = localized(change.columnHeaderL10n);
  const code = change.columnCode ?? null;

  if (header.length > 0) return code === null ? header : `${header} (${code})`;

  return code ?? t('audit.columnGone', { id: change.columnDefId });
}

const CellChangesTable = memo(function CellChangesTable({
  items,
}: {
  readonly items: readonly CellChange[];
}): JSX.Element {
  return (
    <Table striped className="ecr-sticky-head">
      <Table.Thead>
        <Table.Tr>
          <Table.Th>{t('audit.when')}</Table.Th>
          <Table.Th>{t('audit.who')}</Table.Th>
          <Table.Th>{t('audit.document')}</Table.Th>
          <Table.Th>{t('documents.period')}</Table.Th>
          <Table.Th>{t('audit.cell')}</Table.Th>
          <Table.Th>{t('import.was')}</Table.Th>
          <Table.Th>{t('import.becomes')}</Table.Th>
          <Table.Th>{t('audit.origin')}</Table.Th>
        </Table.Tr>
      </Table.Thead>
      <Table.Tbody>
        {items.map((change, index) => (
          <Table.Tr key={`${change.documentId}:${change.rowKey}:${change.columnDefId}:${index}`}>
            <Table.Td>
              {/* ⛔ Саме `Timestamp`, а не сирий рядок: точне значення
                  лишається в `dateTime`/`title`, читабельним стає лише те,
                  що бачить око. */}
              <Timestamp value={change.changedAt} />
              {/* ⚠ Пізня правка — у пільговому строку після кінця періоду
                  (`D-70`); пояснювати доводиться саме її. */}
              {change.isLateEdit && (
                <Badge ml="xs" size="xs" color="statusWarning" variant="light">
                  {t('audit.late')}
                </Badge>
              )}
            </Table.Td>
            {/* ⛔ `R-18`: імена з сервера, а не «user 3 · Document 1 · 2».
                Номер лишається підказкою (`title`) — фільтри журналу
                стоять саме на ньому. */}
            <Table.Td title={`#${String(change.changedByUserId)}`}>{authorName(change)}</Table.Td>
            <Table.Td title={`#${String(change.documentId)}`}>{documentText(change)}</Table.Td>
            <Table.Td>{change.periodKey}</Table.Td>
            <Table.Td>
              <Text size="xs" title={`#${String(change.columnDefId)}`}>
                {change.rowKey} · {columnText(change)}
              </Text>
            </Table.Td>
            <Table.Td>{auditValueText(change.oldValue, change.columnDataType)}</Table.Td>
            <Table.Td>{auditValueText(change.newValue, change.columnDataType)}</Table.Td>
            <Table.Td>
              {/* ⚠ Походження відрізняє руку людини від збору з джерела. */}
              <Badge size="sm" variant="light">
                {change.origin}
              </Badge>
            </Table.Td>
          </Table.Tr>
        ))}
      </Table.Tbody>
    </Table>
  );
});

/**
 * Дата у форматі `YYYY-MM-DD` за N днів до сьогодні.
 *
 * ⚠ Через локальні складники, а не `toISOString()`: той переводить у UTC і
 * ввечері зсуває дату на добу назад — журнал за «сьогодні» показував би
 * учорашній.
 */
function isoDaysAgo(days: number): string {
  const date = new Date();
  date.setDate(date.getDate() - days);

  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');

  return `${date.getFullYear()}-${month}-${day}`;
}
