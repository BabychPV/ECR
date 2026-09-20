import { useMemo, useState, type JSX, type ReactNode } from 'react';
import { Button, Group, ScrollArea, Table, Text, UnstyledButton } from '@mantine/core';
import { AsyncBoundary } from '../AsyncBoundary';
import { decimalEquals, formatLocale, formatNumber, normalizeDecimal } from '@/shared/format';
import { MaxColumns, type DataTableColumn, type SortKey, type SortState } from './types';

/**
 * Таблиця переліку набору (`KIT.md` §6.5, директива №15 §2, Шар 3, крок
 * `UI-06`).
 *
 * ⛔ **Не RevoGrid.** Сітка документа — редактор тисяч комірок із власним
 * вирівнюванням, станом і буфером обміну; перелік із двадцяти рядків, який
 * треба відсортувати й клацнути, нічого з того не потребує. Той самий
 * компонент на обидві задачі означав би, що кожен екран-перелік тягне
 * `@revolist/react-datagrid` у свій чанк (`D-132`: бюджет маршруту 250 КБ,
 * найтісніший уже за 245.6).
 *
 * ⛔ **Стани — через `AsyncBoundary`, а не власний перемикач.** Це головна
 * причина, чому таблиця взагалі компонент, а не сніпет: п'ятнадцять областей
 * уже один раз розійшлися на тому, як виглядає невдалий запит
 * (`DocumentsPage`: «зверху червона смуга, під нею таблиця з заголовками й
 * жодним рядком»). Другий перемикач станів поруч із наявним — це та сама
 * розбіжність, просто заведена наново.
 */

/** Стан таблиці, коли рядків немає (`L10`). */
type EmptyKind = 'empty' | 'filtered';

export interface DataTableProps<Row> {
  /** Колонки. ⛔ Не більше `MaxColumns` (`L5`). */
  readonly columns: readonly DataTableColumn<Row>[];

  /** Рядки поточної сторінки; `undefined` — запиту ще не робили. */
  readonly rows: readonly Row[] | undefined;

  /** Ключ рядка: він же ключ React і значення `selectedKey`. */
  readonly rowKey: (row: Row) => string;

  /** Чи триває запит. */
  readonly isPending?: boolean | undefined;

  /** Помилка запиту. */
  readonly error?: unknown;

  /** Повторити запит — кнопка стану помилки (`ErrorAlert`). */
  readonly onRetry?: (() => void) | undefined;

  /**
   * Чи звужений перелік фільтром.
   *
   * ⛔ `L10`: «фільтр нічого не знайшов» — ОКРЕМИЙ стан, не «порожньо».
   * Відрізнити їх може лише викликач: сама таблиця бачить порожній масив і в
   * тому, і в тому випадку.
   */
  readonly filtered?: boolean | undefined;

  /** Заголовок порожнього стану: ЩО саме порожнє. */
  readonly emptyTitle?: string | undefined;

  /** Пояснення порожнього стану: ЧОМУ порожньо і що робити. */
  readonly emptyHint?: string | undefined;

  /** Дія порожнього стану; ховає її викликач, коли права немає. */
  readonly emptyAction?: ReactNode;

  /** Заголовок стану «фільтр нічого не знайшов». */
  readonly noMatchTitle?: string | undefined;

  /** Пояснення стану «фільтр нічого не знайшов». */
  readonly noMatchHint?: string | undefined;

  /** Скинути фільтри зі стану «фільтр нічого не знайшов». */
  readonly onClearFilters?: (() => void) | undefined;

  /** Підпис кнопки скидання фільтрів; див. `ClearGlyph`. */
  readonly clearFiltersLabel?: string | undefined;

  /**
   * Скільки рядків є на сервері ВСЬОГО.
   *
   * ⚠ Без цього числа підсумок «N / M» не малюється зовсім (`D15-06`):
   * «25 / 25» на першій сторінці курсорної пагінації було б неправдою.
   */
  readonly total?: number | undefined;

  /** Довантажити наступну сторінку (курсорна пагінація сервера). */
  readonly onShowMore?: (() => void) | undefined;

  /** Підпис кнопки «показати ще»; див. `MoreGlyph`. */
  readonly showMoreLabel?: string | undefined;

  /** Чи вантажиться наступна сторінка просто зараз. */
  readonly isFetchingMore?: boolean | undefined;

  /** Клац по рядку — зазвичай відкриття шухляди подробиць. */
  readonly onRowClick?: ((row: Row) => void) | undefined;

  /**
   * `aria-label` рядка: чим цей рядок є для того, хто не бачить екрана.
   *
   * ⛔ Повертає `null` для рядка, якому власне ім'я НЕ потрібне — і це не
   * зручність, а вимога. `aria-label` на `<tr>` ЗАМІНЮЄ собою читання
   * клітинок, тож підпис виду «назва джерела» на кожному рядку відібрав би в
   * читалки решту колонок і лишив саму назву. Ім'я ставлять там, де рядок
   * несе щось понад свої клітинки (позначка «вимкнене», яку видно лише
   * кольором чи прозорістю).
   *
   * ⚠ Тип розширено `null` після ПЕРШОГО справжнього споживача
   * (`SourcesPage`): із сигнатурою `=> string` єдиним способом «не ставити
   * підпис» був порожній рядок, а `aria-label=""` — це теж атрибут, і
   * поводиться він саме так, як описано вище.
   */
  readonly rowLabel?: ((row: Row) => string | null) | undefined;

  /** Ключ виділеного рядка. */
  readonly selectedKey?: string | undefined;

  /** Підпис таблиці (`<caption>`). */
  readonly caption?: string | undefined;

  /** Висота області прокручування; без неї — висота за вмістом (`auto`). */
  readonly height?: number | string | undefined;
}

/**
 * Підпис кнопки «показати ще» за замовчуванням.
 *
 * ⛔ Це СИМВОЛ, а не англійський літерал, і причина процедурна: рядки
 * інтерфейсу живуть у каталозі на сервері (`09-seed.sql`), ключа під «Show
 * more» там немає, а завести його цим PR не можна — сід є спільним ресурсом
 * паралельної фази (правило 2 «Правил розробки»). Лишалося три виходи:
 * `t('list.showMore')` на неіснуючий ключ (читалка почула б `⟦list.showMore⟧`
 * — `D-138`), англійське слово, зашите в код усіх трьох мов продукту, або
 * гліф, однаковий у кожній із них. Обрано третє.
 *
 * ⚠ Це названа прогалина, а не рішення назавжди: щойно ключ з'явиться,
 * викликач передає `showMoreLabel={t('…')}` — і жоден рядок цього файлу не
 * змінюється.
 */
const MoreGlyph = '⌄';

/** Підпис кнопки скидання фільтрів за замовчуванням; причина — у `MoreGlyph`. */
const ClearGlyph = '×';

/** Значення поля `key` у рядку, коли колонка не задала власного `render`. */
function fieldOf<Row>(row: Row, key: string): unknown {
  if (row === null || typeof row !== 'object') return undefined;

  return (row as Record<string, unknown>)[key];
}

/** Значення, за яким колонка порівнюється. */
function sortKeyOf<Row>(column: DataTableColumn<Row>, row: Row): SortKey {
  if (column.sortValue !== undefined) return column.sortValue(row);

  const raw = fieldOf(row, column.key);

  if (raw === null || raw === undefined) return raw;
  if (typeof raw === 'number' || typeof raw === 'string' || typeof raw === 'boolean') return raw;

  // Об'єкт, масив, функція — скалярного порядку не мають; сортувати нема за що.
  return undefined;
}

/** Чи вважається значення відсутнім для сортування. */
function isMissing(value: SortKey): boolean {
  return value === null || value === undefined || value === '';
}

/**
 * Десяткове з API приїжджає РЯДКОМ (`e470777a`), і для числової колонки це
 * означає дві різні поломки одразу.
 *
 * ⛔ Сортування: `'10' < '9'` за будь-яким колатором — тобто десяткова колонка
 * впорядковується як текст, і «найбільше значення» вгорі ним не є. Числову
 * гілку `compareKeys` рятує `typeof a === 'number'`, а рядок до неї не доходить.
 *
 * ⛔ Показ: `typeof raw === 'number'` так само не спрацьовує, і замість
 * `1,234.5` у клітинці лишається сире `1234.5000000000` — без роздільників
 * розрядів і з хвостом нулів масштабу колонки.
 *
 * ⚠ Обидві гілки вмикає `column.num`, а не «рядок схожий на число»: код виду
 * `'007'` теж нормалізується в число, і автоматичне розпізнавання перетворило б
 * його на `7` та переставило б колонку кодів у числовому порядку.
 */

/** Скільки знаків у дробовій частині канонічного рядка. */
function scaleOf(canonical: string): number {
  const dot = canonical.indexOf('.');

  return dot < 0 ? 0 : canonical.length - dot - 1;
}

/**
 * Канонічний десятковий рядок як ціле число заданого масштабу.
 *
 * ⚠ `bigint`, а не `number`: рівно щоб не втратити те, заради чого сервер і
 * перевів `decimal` у рядок. `decimal(28,16)` не вміщається в IEEE-754.
 */
function scaled(canonical: string, scale: number): bigint {
  const dot = canonical.indexOf('.');
  const digits = dot < 0 ? canonical : canonical.slice(0, dot) + canonical.slice(dot + 1);

  return BigInt(digits + '0'.repeat(scale - scaleOf(canonical)));
}

/**
 * Порядок двох десяткових рядків; `null` — принаймні один із них не десятковий.
 *
 * ⛔ Розбір — лише `normalizeDecimal`, рівність — лише `decimalEquals`
 * (`shared/format/decimal.ts`): та сама одиниця приходить як `'1'`, `'1.0'` або
 * `'1.0000000000'` залежно від масштабу колонки, і другий нормалізатор,
 * написаний тут, розійшовся б із першим мовчки.
 */
function compareDecimals(left: string, right: string): number | null {
  const a = normalizeDecimal(left);
  const b = normalizeDecimal(right);

  if (a === null || b === null) return null;
  if (decimalEquals(a, b)) return 0;

  const scale = Math.max(scaleOf(a), scaleOf(b));

  return scaled(a, scale) < scaled(b, scale) ? -1 : 1;
}

/**
 * Пам'ять форматувальників — з тієї ж причини, що в `shared/format/number.ts`.
 *
 * ⚠ Зберігається сам `format`, а не об'єкт: `Intl.NumberFormat.prototype.format`
 * — це аксесор, який віддає ВЖЕ ЗВ'ЯЗАНУ функцію, тож відчепити її безпечно.
 */
const decimalFormatters = new Map<string, (value: string) => string>();

/**
 * Канонічний десятковий рядок — локаллю продукту, без проходу через `Number`.
 *
 * ⛔ Опцій немає навмисно: числова клітинка поруч малюється `formatNumber(raw)`
 * так само без опцій, і друга політика дробової частини дала б в одній колонці
 * два різні формати того самого поняття.
 *
 * ⚠ Приведення типу потрібне лише компіляторові: `format` приймає десятковий
 * РЯДОК із `Intl.NumberFormat` v3 (перевірено в цьому середовищі —
 * `__tests__/DataTable.decimal.test.tsx`, «Intl приймає рядок»), але
 * `tsconfig.json` стоїть на `lib: ES2022`, де цього перевантаження ще немає.
 */
function formatDecimal(canonical: string): string {
  const locale = formatLocale();
  const hit = decimalFormatters.get(locale);
  if (hit !== undefined) return hit(canonical);

  const made = new Intl.NumberFormat(locale).format as unknown as (value: string) => string;
  decimalFormatters.set(locale, made);

  return made(canonical);
}

/**
 * Порівняння двох значень колонки з урахуванням напрямку.
 *
 * ⛔ Напрямок застосовується ВСЕРЕДИНІ, а не множенням результату ззовні, і
 * саме через пропуски: порожні значення мусять лишатися В КІНЦІ і при
 * зростанні, і при спаданні. Зовнішнє `* -1` підняло б їх нагору — тобто
 * перше ж сортування за колонкою з пропусками ховало б усі заповнені рядки
 * під купою порожніх, роблячи рівно протилежне тому, навіщо на неї клацнули.
 *
 * ⚠ Рядки порівнює `Intl.Collator` локаллю ПРОДУКТУ, а не оператор `<`: у
 * казахській і російській порядок літер не збігається з кодами UTF-16, і
 * `'Ә' < 'Б'` дало б порядок, якого не існує в жодній абетці.
 *
 * ⚠ `numeric` (`column.num`) вмикає ЧИСЛОВЕ порівняння десяткових рядків, і
 * лише воно: у текстовій колонці той самий перемикач переставив би коди.
 * Нерозпізнане значення числової колонки спадає на колатор — інакше рядок, що
 * числом не є, зник би з порядку зовсім.
 */
function compareKeys(
  a: SortKey,
  b: SortKey,
  sign: number,
  collator: Intl.Collator,
  numeric: boolean,
): number {
  const aMissing = isMissing(a);
  const bMissing = isMissing(b);

  if (aMissing || bMissing) {
    if (aMissing && bMissing) return 0;

    return aMissing ? 1 : -1;
  }

  if (typeof a === 'number' && typeof b === 'number') return (a - b) * sign;
  if (typeof a === 'boolean' && typeof b === 'boolean') return (Number(a) - Number(b)) * sign;

  if (numeric) {
    const order = compareDecimals(String(a), String(b));

    if (order !== null) return order * sign;
  }

  return collator.compare(String(a), String(b)) * sign;
}

/** Наступний стан сортування за клацанням: зростання → спадання → як було. */
function nextSort(current: SortState | null, key: string): SortState | null {
  if (current === null || current.key !== key) return { key, direction: 'asc' };
  if (current.direction === 'asc') return { key, direction: 'desc' };

  return null;
}

/** Значення `aria-sort` для шапки колонки. */
function ariaSort(current: SortState | null, key: string): 'ascending' | 'descending' | 'none' {
  if (current === null || current.key !== key) return 'none';

  return current.direction === 'asc' ? 'ascending' : 'descending';
}

/** Чи можна сортувати за цією колонкою. */
function isSortable<Row>(column: DataTableColumn<Row>): boolean {
  return column.sortable !== false;
}

export function DataTable<Row>({
  columns,
  rows,
  rowKey,
  isPending = false,
  error = null,
  onRetry,
  filtered = false,
  emptyTitle,
  emptyHint,
  emptyAction,
  noMatchTitle,
  noMatchHint,
  onClearFilters,
  clearFiltersLabel = ClearGlyph,
  total,
  onShowMore,
  showMoreLabel = MoreGlyph,
  isFetchingMore = false,
  onRowClick,
  rowLabel,
  selectedKey,
  caption,
  height,
}: DataTableProps<Row>): JSX.Element {
  /*
   * ⛔ `L5` кидає ВИНЯТОК у режимі розробки, а не пише в консоль (`KIT.md`
   * §6.5 каже «попередження в консолі» — директива №15 §0 це свідомо
   * посилила). Причина названа в самій директиві: `src/test/setup.ts` консоль
   * НЕ стереже, тож попередження не побачив би ніхто — ні в наборі, ні в
   * жодному з семи гейтів. Правило, яке не вміє почервоніти, — не правило.
   *
   * ⛔ У зібраному застосунку — НЕ кидати. Восьма колонка не робить сторінку
   * непрацездатною: вона робить її незручною. Білий екран замість переліку
   * документів через те, що хтось додав колонку, коштував би користувачеві
   * дорожче за саму ваду, яку правило ловить.
   *
   * ⚠ Перевірка в тілі рендера, а не в модулі: колонки приходять пропом і
   * можуть змінитися (перемикач «показати технічні поля»).
   */
  if (import.meta.env.DEV && columns.length > MaxColumns) {
    throw new Error(
      `DataTable: ${String(columns.length)} колонок, межа L5 — ${String(MaxColumns)}. ` +
        'Решта полів належить шухляді подробиць рядка (KIT.md §1.7, директива №15 §0).',
    );
  }

  const [sort, setSort] = useState<SortState | null>(null);

  const collator = useMemo(() => new Intl.Collator(formatLocale()), []);

  const sorted = useMemo<readonly Row[] | undefined>(() => {
    if (rows === undefined || sort === null) return rows;

    const column = columns.find((candidate) => candidate.key === sort.key);
    if (column === undefined || !isSortable(column)) return rows;

    const sign = sort.direction === 'asc' ? 1 : -1;

    /*
     * ⛔ Копія, а не `rows.sort()`: масив належить кешу TanStack Query, і
     * сортування на місці мовчки перемішало б дані, які інші споживачі того
     * самого ключа вважають незмінними.
     */
    return [...rows].sort((left, right) =>
      compareKeys(
        sortKeyOf(column, left),
        sortKeyOf(column, right),
        sign,
        collator,
        column.num === true,
      ),
    );
  }, [rows, sort, columns, collator]);

  const kind: EmptyKind = filtered ? 'filtered' : 'empty';

  /*
   * ⛔ `L10`: «порожньо» і «фільтр нічого не знайшов» — РІЗНІ екрани. Різниця
   * не косметична: у першому випадку працювати ще нема з чим і доречна дія
   * «створити», у другому дані є, і єдина корисна дія — скинути фільтр.
   * Однаковий екран на обидва випадки веде користувача створювати те, що в
   * нього вже є.
   */
  const clearButton =
    onClearFilters === undefined ? undefined : (
      <Button variant="default" size="xs" onClick={onClearFilters} data-table-clear-filters="true">
        {clearFiltersLabel}
      </Button>
    );

  const stateTitle = kind === 'filtered' ? noMatchTitle : emptyTitle;
  const stateHint = kind === 'filtered' ? noMatchHint : emptyHint;
  const stateAction = kind === 'filtered' ? clearButton : emptyAction;

  const shown = sorted?.length ?? 0;

  /*
   * ⚠ Кнопка «показати ще» зникає, щойно показано все: кнопка, яка нічого не
   * додає, — це той самий тупиковий екран, що й «повторити» на відмові в
   * праві (`AsyncBoundary`).
   */
  const hasMore = onShowMore !== undefined && total !== undefined && shown < total;

  return (
    <div data-table-state={isPending ? 'loading' : shown > 0 ? 'data' : kind}>
      <AsyncBoundary<readonly Row[]>
        isPending={isPending}
        error={error}
        data={sorted}
        isEmpty={(page) => page.length === 0}
        emptyTitle={stateTitle}
        emptyHint={stateHint}
        emptyAction={stateAction}
        skeleton="table"
        onRetry={onRetry}
      >
        {(page) => (
          <>
            {/*
             * ⚠ `ScrollArea` навколо таблиці (`KIT.md` §6.5: «таблиця живе у
             * власному overflow»), а не прокрутка сторінки: інакше закріплена
             * шапка закріплювалася б відносно вікна, і при горизонтальній
             * прокрутці вузького екрана шапка їхала б окремо від даних.
             */}
            <ScrollArea type="auto" {...(height === undefined ? {} : { h: height })}>
              {/*
               * ⚠ Закріплення шапки — КЛАСОМ `ecr-sticky-head`
               * (`shared/theme/motion.css`), а не власним стилем: цей клас уже
               * стоїть на двадцяти таблицях застосунку, і друге закріплення,
               * написане тут, розійшлося б із ним на першій же зміні токена
               * тла.
               */}
              <Table striped highlightOnHover className="ecr-sticky-head">
                {caption !== undefined && <Table.Caption>{caption}</Table.Caption>}

                <Table.Thead>
                  <Table.Tr>
                    {columns.map((column) => (
                      <HeaderCell
                        key={column.key}
                        column={column}
                        sort={sort}
                        onSort={() => {
                          setSort((current) => nextSort(current, column.key));
                        }}
                      />
                    ))}
                  </Table.Tr>
                </Table.Thead>

                <Table.Tbody>
                  {page.map((row) => {
                    const key = rowKey(row);

                    return (
                      <Table.Tr
                        key={key}
                        data-row-key={key}
                        data-selected={selectedKey === key ? 'true' : undefined}
                        // ⚠ `?? undefined`, а не `?? ''`: порожній рядок теж
                        // ставить атрибут, а `aria-label=""` на `<tr>` лишає
                        // рядок без доступного імені замість того, щоб дати
                        // читалці прочитати клітинки.
                        aria-label={(rowLabel === undefined ? null : rowLabel(row)) ?? undefined}
                        style={{ cursor: onRowClick === undefined ? 'default' : 'pointer' }}
                        onClick={
                          onRowClick === undefined
                            ? undefined
                            : () => {
                                onRowClick(row);
                              }
                        }
                      >
                        {columns.map((column) => (
                          <Table.Td
                            key={column.key}
                            align={column.num === true ? 'right' : undefined}
                            {...(column.num === true || column.mono === true
                              ? { ff: 'monospace' }
                              : {})}
                          >
                            <Cell column={column} row={row} />
                          </Table.Td>
                        ))}
                      </Table.Tr>
                    );
                  })}
                </Table.Tbody>
              </Table>
            </ScrollArea>

            {/*
             * «Showing N of M · Show more» (`KIT.md` §6.5) поверх КУРСОРНОЇ
             * пагінації сервера: сторінка не має номера, тому й переліку
             * сторінок тут немає — лише «скільки вже видно» і «дати ще».
             *
             * ⛔ `D15-06`: без `total` не малюється нічого. Підсумок «25 / 25»
             * на першій сторінці курсорної вибірки був би неправдою, а не
             * заглушкою.
             */}
            {total !== undefined && (
              <Group justify="center" gap="xs" mt="sm">
                <Text size="sm" c="dimmed" data-table-count="true">
                  {`${formatNumber(shown)} / ${formatNumber(total)}`}
                </Text>

                {hasMore && (
                  <Button
                    variant="default"
                    size="xs"
                    loading={isFetchingMore}
                    onClick={onShowMore}
                    data-table-show-more="true"
                  >
                    {showMoreLabel}
                  </Button>
                )}
              </Group>
            )}
          </>
        )}
      </AsyncBoundary>
    </div>
  );
}

/**
 * Шапка однієї колонки.
 *
 * ⚠ Стан сортування несе `aria-sort` — атрибут стандарту, який читалка
 * оголошує сама. Стрілка поруч — `aria-hidden`: інакше ім'я кнопки мінялося б
 * із «Код» на «Код ▲», і читалка щоразу перечитувала б колонку заново, хоча
 * змінився лише напрямок.
 */
function HeaderCell<Row>({
  column,
  sort,
  onSort,
}: {
  readonly column: DataTableColumn<Row>;
  readonly sort: SortState | null;
  readonly onSort: () => void;
}): JSX.Element {
  const sortable = isSortable(column);
  const active = sort !== null && sort.key === column.key;

  return (
    <Table.Th
      scope="col"
      title={column.title}
      aria-sort={sortable ? ariaSort(sort, column.key) : undefined}
      {...(column.minWidth === undefined ? {} : { miw: column.minWidth })}
    >
      {sortable ? (
        <UnstyledButton
          type="button"
          onClick={onSort}
          data-sort-key={column.key}
          data-sort-active={String(active)}
        >
          {column.label}
          {active && <span aria-hidden="true">{sort.direction === 'asc' ? ' ▲' : ' ▼'}</span>}
        </UnstyledButton>
      ) : (
        column.label
      )}
    </Table.Th>
  );
}

/**
 * Вміст клітинки.
 *
 * ⛔ `D15-06`: елемента без даних НЕ МАЛЮЄМО — ні прочерку, ні заглушки.
 * `KIT.md` §6.5 приписує колонці малювати «—» на `null`; директива №15 це
 * знімає, і різниця змістовна: прочерк — це твердження «тут порожньо», яке в
 * половині випадків неправда (значення не завантажилося, поле не входить у
 * проєкцію переліку). Там, де відсутність справді треба показати, її показує
 * сама колонка своїм `render` — як це вже робить `Timestamp` зі своїм
 * `fallback`.
 */
function Cell<Row>({
  column,
  row,
}: {
  readonly column: DataTableColumn<Row>;
  readonly row: Row;
}): JSX.Element | null {
  if (column.render !== undefined) return <>{column.render(row)}</>;

  const raw = fieldOf(row, column.key);

  if (raw === null || raw === undefined) return null;

  if (typeof raw === 'number') return <>{formatNumber(raw)}</>;

  if (typeof raw === 'string') {
    // ⛔ Лише числова колонка: `'007'` у колонці кодів теж нормалізується — і
    // поїхав би на екран сімкою.
    if (column.num === true) {
      const canonical = normalizeDecimal(raw);

      if (canonical !== null) return <>{formatDecimal(canonical)}</>;
    }

    return <>{raw}</>;
  }

  // Булеве, об'єкт, масив: скалярного показу не мають — малює `render`.
  return null;
}
