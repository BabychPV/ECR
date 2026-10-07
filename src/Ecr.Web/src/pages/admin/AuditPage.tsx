import { lazy, Suspense, useRef, type JSX } from 'react';
import {
  Badge,
  Button,
  Checkbox,
  NumberInput,
  SegmentedControl,
  Select,
  Skeleton,
  TextInput,
} from '@mantine/core';
import type { CellChangePage } from '@/api/types';
import { useAuthorOptions } from '@/features/audit/authorOptions';
import { CellChangesTable, originLabel } from '@/features/audit/CellChangesTable';
import { cellChangeOrigins, cellChangesQuery, isSingleCell, useCellChanges } from '@/features/audit/api';
import { FilterHints, FilterInline, FilterRow, readerOnlyDescription } from '@/shared/ui/FilterBar';
import { SecurityEventsPanel } from '@/features/audit/SecurityEventsPanel';
import { StructureChangesPanel } from '@/features/audit/StructureChangesPanel';
import { StructureExportButton } from '@/features/audit/StructureExportButton';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { useDebouncedFilter, useFilterCursor } from '@/shared/ui/useDebouncedFilter';
import { useFieldDraft } from '@/shared/ui/useFieldDraft';
import { useUrlNumber, useUrlParamsSetter, useUrlState } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';
import { todayDateOnly } from '@/shared/format';

export { auditValueText } from '@/features/audit/CellChangesTable';

/** A1-02: поле дати — за `import()` (`D-132`), той самий прийом, що `DocumentHeaderPanel`. */
const DateOnlyInput = lazy(async () => ({
  default: (await import('@/shared/dates/DateInputWithStyles')).DateOnlyInput,
}));

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
  // ФВ-5.24: третя вкладка — журнал подій безпеки (`?view=security`), з тих самих міркувань.
  const security = view === 'security';
  const other = structure || security;
  const changes = useCellChanges(filter, !other);

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
          <FilterRow>
            <FilterInline>
              <SegmentedControl
                aria-label={t('audit.title')}
                value={structure ? 'structure' : security ? 'security' : 'cells'}
                onChange={(value) => setView(value === 'structure' || value === 'security' ? value : null)}
                data={[
                  { value: 'cells', label: t('audit.viewCells') },
                  { value: 'structure', label: t('audit.viewStructure') },
                  { value: 'security', label: t('audit.viewSecurity') },
                ]}
              />
            </FilterInline>
            <Suspense fallback={<Skeleton height="var(--ecr-ctl-height)" width={140} />}>
              <DateOnlyInput
                size="xs"
                label={t('audit.from')}
                value={fromField.value}
                onFocus={fromField.onFocus}
                onBlur={fromField.onBlur}
                onChange={(value) => {
                  fromField.setValue(value);
                  setFrom(value);
                }}
              />
            </Suspense>
            <Suspense fallback={<Skeleton height="var(--ecr-ctl-height)" width={140} />}>
              <DateOnlyInput
                size="xs"
                label={t('audit.to')}
                value={toField.value}
                onFocus={toField.onFocus}
                onBlur={toField.onBlur}
                onChange={(value) => {
                  toField.setValue(value);
                  setTo(value);
                }}
              />
            </Suspense>
            <NumberInput
              size="xs"
              miw={140}
              // ⚠ На вкладці структурних змін документа немає: мертвий фільтр
              // читався б як «за цим документом змін не було».
              display={other ? 'none' : undefined}
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
          </FilterRow>
        }
      />

      {structure && <StructureExportButton from={fromDate} to={toDate} />}
      {structure && <StructureChangesPanel key={`${fromDate}:${toDate}`} from={fromDate} to={toDate} />}
      {security && <SecurityEventsPanel key={`${fromDate}:${toDate}`} from={fromDate} to={toDate} />}

      {/* ⚠ Відступ усередині цієї обгортки НАВМИСНО не зсунуто: зсув — це
          форматування 160 рядків, а воно йде окремим PR (CLAUDE.md, правило 4). */}
      {!other && (
      <>
      {/* ⚠ Фільтри ОКРЕМИМ рядком, а не в шапці: їх шість, і в шапці вони
          витіснили б заголовок за край на ноутбучній ширині. */}
      <FilterRow mb="xs" data-audit-filter-row="cells">
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
          // UI-38: походження словом макета («Typed by a user», «Excel import»…), значення — код сервера.
          data={cellChangeOrigins.map((value) => ({ value, label: originLabel(value) }))}
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
        <FilterInline>
          <Checkbox
            label={t('audit.lateOnly')}
            checked={lateOnly === 'true'}
            onChange={(event) => {
              setLateOnly(event.currentTarget.checked ? 'true' : null);
            }}
          />
        </FilterInline>
        {/* ⚠ Значок «історія однієї комірки» — не прикраса: саме в цьому стані
            сервер приймає запит за `Document.View` і з вікном у 13 місяців, а
            не за `Security.ViewAudit` і 92 дні. Без видимої ознаки людина не
            розуміє, чому те саме вікно то приймається, то ні. */}
        {single && (
          <FilterInline>
            <Badge size="sm" variant="light" color="brand">
              {t('audit.cell')}
            </Badge>
          </FilterInline>
        )}
        <FilterInline>
          <Button
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
        </FilterInline>
      </FilterRow>

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
            {/* ⛔ `memo`-таблиця (`R-18`), форма макета `UI-38` — див. `CellChangesTable`. */}
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

/** Останні отримані дані — поки нові ще в дорозі; відмова скидає запам'ятоване. */
function useLastData<T>(data: T | undefined, healthy: boolean): T | undefined {
  const last = useRef<T | undefined>(undefined);

  if (!healthy) last.current = undefined;
  else if (data !== undefined) last.current = data;

  return data ?? last.current;
}

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

  return todayDateOnly(date);
}
