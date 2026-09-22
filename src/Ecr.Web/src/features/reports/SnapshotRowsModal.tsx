import { Fragment, type JSX, type ReactNode } from 'react';
import { Button, Group, Modal, ScrollArea, Table, Text } from '@mantine/core';
import { useInfiniteQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';
import { formatDecimal, formatNumber } from '@/shared/format';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { Timestamp } from '@/shared/ui/Timestamp';
import { t } from '@/shared/i18n';

type SnapshotRowsPage = components['schemas']['SnapshotRowsPage'];
type SnapshotColumn = components['schemas']['SnapshotColumn'];
type SnapshotRowItem = components['schemas']['SnapshotRow'];
type SnapshotRowGroup = components['schemas']['SnapshotRowGroup'];
type SnapshotTotal = components['schemas']['SnapshotTotal'];

/** Рядків на сторінку: перші сто, далі — «показати ще». */
const PageSize = 100;

/** Тип колонки, у якої значення — дата (`SnapshotColumn.Kind`). */
const DateKind = 'date';

/** Тип колонки, у якої значення — число. */
const NumberKind = 'number';

/** Функція підсумку «кількість непорожніх значень» (`ReportLayout.Count`). */
const CountFn = 'count';

/**
 * Рядки зрізу в застосунку (`D-52a`): другий споживач `rpt.*` поруч із SSRS.
 *
 * ⚠ Колонки приходять із сервера — з опису версії, за якою зріз побудовано, —
 * тож таблиця нічого не знає про конкретний звіт. Заголовок колонки — її код:
 * назв мовами каталогу в описі ще немає.
 *
 * ⚠ Макет (`R8`) — групи й підсумки — рахує СЕРВЕР по всьому зрізу й віддає
 * готовим на кожній сторінці (`IReportSnapshotBuilder.SnapshotRowsPage`). Тут
 * він лише малюється, рівно як у книзі (`SnapshotWorkbookWriter`): другий
 * рахівник розійшовся б із першим мовчки, і ніхто б не знав, котрому вірити.
 *
 * ⛔ Версія без макета — це ТРЕТІЙ стан, не відмова й не порожнеча: `groups`
 * і `totals` приходять `null`, і таблиця лишається рівно такою, якою була до
 * `R8`. Порожній заголовок групи чи «Разом: —» там означали б, що зріз щось
 * підсумовує, хоча підсумовувати його ніхто не просив.
 */
export default function SnapshotRowsModal(props: {
  snapshotId: number | null;
  onClose: () => void;
}): JSX.Element {
  const { snapshotId } = props;

  const pages = useInfiniteQuery({
    queryKey: ['snapshot-rows', snapshotId],
    queryFn: ({ pageParam }) =>
      apiFetch<SnapshotRowsPage>(
        `/api/v1/reports/snapshots/${snapshotId ?? 0}/rows?limit=${PageSize}` +
          (pageParam === null ? '' : `&cursor=${pageParam}`),
      ),
    initialPageParam: null as number | null,
    getNextPageParam: (last) => last.nextCursor,
    enabled: snapshotId !== null,
  });

  /*
   * ⚠ Макет береться з ПЕРШОЇ сторінки, а не зводиться по всіх: сервер прямо
   * обіцяє, що групи й підсумки однакові на кожній сторінці, бо пораховані по
   * всьому зрізу («підсумок, що міняється від сторінки до сторінки, не є
   * підсумком»). Складати їх удруге тут означало б рахувати по тому, що вже
   * довантажили, — тобто показувати інше число, ніж книга.
   */
  const first = pages.data?.pages[0];
  const columns = first?.columns ?? [];
  const groups = first?.groups ?? [];
  const totals = first?.totals ?? [];
  const rows = (pages.data?.pages ?? []).flatMap((page) => page.rows);

  return (
    <Modal
      opened={snapshotId !== null}
      onClose={props.onClose}
      title={t('snapshots.rowsTitle')}
      size="xl"
    >
      <AsyncBoundary<typeof rows>
        isPending={pages.isPending}
        error={pages.error}
        data={pages.data === undefined ? undefined : rows}
        isEmpty={(all) => all.length === 0}
        emptyTitle={t('snapshots.rowsEmpty')}
        skeleton="table"
        onRetry={() => void pages.refetch()}
      >
        {(all) => (
          <ScrollArea type="auto" offsetScrollbars>
            <Table striped>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>#</Table.Th>
                  {/*
                    ⛔ `R9`: підпис колонки, а не її код. `column.name` приходить
                    із сервера вже з розгорнутим фолбеком «мова запиту → en →
                    код» (`ReportColumnNames`) і НІКОЛИ не порожній — опис без
                    назв підписує колонку її кодом. Тому власного фолбеку тут
                    немає й бути не повинно: друга копія того самого правила
                    розійшлася б із першою, і шапка на екрані перестала б
                    збігатися з шапкою книги XLSX, яку читає регулятор.

                    ⚠ `key` лишається КОДОМ: він унікальний за контрактом
                    (`SnapshotRow.Cells` індексується ним), а підпис — ні; дві
                    колонки з однаковою назвою різними мовами цілком законні.
                  */}
                  {columns.map((column) => (
                    <Table.Th key={column.code} ta={column.kind === NumberKind ? 'right' : undefined}>
                      {column.name}
                    </Table.Th>
                  ))}
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {blocksOf(all, groups).map((block, index) => (
                  <Fragment key={block.group === null ? `rest-${String(index)}` : `group-${String(index)}`}>
                    {block.group !== null && (
                      <Table.Tr>
                        {/*
                          ⚠ Заголовок групи малюється ЗАВЖДИ, коли групи є, —
                          `ShowGroupHeader` сторінки тут навмисно не питається.
                          Прапорець про КНИГУ: зайвий рядок посеред плаского
                          аркуша ламає прямокутник даних, який потім читають
                          машиною. На екрані ціна протилежна — з макетом рядки
                          йдуть у порядку груп, а не за `RowNo` (`#` стрибає), і
                          без підпису це виглядає як переплутаний порядок.
                        */}
                        <Table.Th colSpan={columns.length + 1} scope="colgroup">
                          {t('snapshots.rowsGroup', { column: block.group.column })}
                          {': '}
                          {valueNode(block.group.value, columnOf(columns, block.group.column))}
                        </Table.Th>
                      </Table.Tr>
                    )}

                    {block.rows.map((row) => (
                      <Table.Tr key={row.rowNo}>
                        <Table.Td>{row.rowNo}</Table.Td>
                        {columns.map((column) => (
                          <Table.Td key={column.code} ta={column.kind === NumberKind ? 'right' : undefined}>
                            {cellText(row.cells[column.code], column.code, column.kind)}
                          </Table.Td>
                        ))}
                      </Table.Tr>
                    ))}

                    {block.group !== null &&
                      totalRows(
                        block.group.totals,
                        columns,
                        t('snapshots.rowsTotalGroup'),
                        `group-${String(index)}`,
                      )}
                  </Fragment>
                ))}

                {totalRows(totals, columns, t('snapshots.rowsTotalAll'), 'all')}
              </Table.Tbody>
            </Table>
          </ScrollArea>
        )}
      </AsyncBoundary>

      {pages.hasNextPage && (
        <Group justify="center" mt="md">
          <Button
            size="xs"
            variant="default"
            loading={pages.isFetchingNextPage}
            onClick={() => void pages.fetchNextPage()}
          >
            {t('snapshots.rowsMore')}
          </Button>
        </Group>
      )}

      {pages.data !== undefined && (
        <Text size="xs" c="dimmed" mt="xs">
          {formatNumber(rows.length)}
        </Text>
      )}
    </Modal>
  );
}

/** Смуга таблиці: рядки однієї групи або рядки поза групами. */
interface RowBlock {
  /** Група цих рядків; `null` — рядки поза макетом. */
  readonly group: SnapshotRowGroup | null;

  /** Рядки смуги в порядку показу. */
  readonly rows: readonly SnapshotRowItem[];
}

/**
 * Розкладає ВЖЕ ЗАВАНТАЖЕНІ рядки по групах.
 *
 * ⛔ Єдине, що пов'язує рядок із групою, — ПОРЯДОК і `rowCount`: у самому
 * рядку ознаки групи немає (сервер не дублює значення колонки в кожну комірку,
 * а `rows` приходять «у порядку груп»). Тому групи розбираються послідовно, і
 * кожна відкушує рівно свої `rowCount` рядків.
 *
 * ⚠ Групи описують УВЕСЬ зріз, а на екрані лежать лише завантажені сторінки —
 * тож група, до рядків якої «показати ще» ще не дійшло, не малюється зовсім.
 * Заголовок із порожнечею під ним читався б як порожня група.
 */
function blocksOf(
  rows: readonly SnapshotRowItem[],
  groups: readonly SnapshotRowGroup[],
): RowBlock[] {
  // ⛔ Третій стан: групування версія не оголошує — жодної смуги, жодного
  // заголовка, таблиця та сама, що й до `R8`.
  if (groups.length === 0) return [{ group: null, rows }];

  const blocks: RowBlock[] = [];
  let taken = 0;

  for (const group of groups) {
    if (taken >= rows.length) break;

    const slice = rows.slice(taken, taken + group.rowCount);
    taken += slice.length;
    blocks.push({ group, rows: slice });
  }

  // ⚠ Рядки, які в жодну групу не потрапили (макет зіпсований, сума `rowCount`
  // менша за кількість рядків), показуються, а не зникають: сховати рядок
  // зрізу гірше, ніж показати його без групи.
  if (taken < rows.length) blocks.push({ group: null, rows: rows.slice(taken) });

  return blocks;
}

/**
 * Підсумкові рядки — по одному на підсумок.
 *
 * ⛔ Кожен підсумок НАЗВАНИЙ двічі: підписом смуги в першій колонці (по чому
 * рахували — по групі чи по всьому зрізу) і назвою функції поруч зі значенням
 * («Сума», «Середнє»). Саме значення лягає в КОЛОНКУ свого підсумку — те саме
 * рішення, що в книзі. Без цих трьох ознак рядок чисел під таблицею читається
 * як ще один рядок звіту.
 */
function totalRows(
  totals: readonly SnapshotTotal[],
  columns: readonly SnapshotColumn[],
  label: string,
  keyPrefix: string,
): JSX.Element[] {
  return totals.flatMap((total, index) => {
    const at = columns.findIndex((column) => column.code === total.column);

    // ⚠ Підсумок над колонкою, якої в таблиці немає: те саме рішення, що в
    // книзі (`SnapshotWorkbookWriter.Totals` — `continue`). Поставити число в
    // чужу колонку означало б показати його як значення тієї колонки.
    if (at < 0) return [];

    return [
      <Table.Tr key={`${keyPrefix}-total-${String(index)}`}>
        <Table.Th scope="row">{label}</Table.Th>
        {columns.map((column, position) => (
          <Table.Td key={column.code} ta={column.kind === NumberKind ? 'right' : undefined}>
            {position === at && (
              <>
                <Text component="span" size="sm" c="dimmed">
                  {totalFnLabel(total.fn)}
                </Text>
                {': '}
                {totalValueNode(total, column)}
              </>
            )}
          </Table.Td>
        ))}
      </Table.Tr>,
    ];
  });
}

/**
 * Підпис функції підсумку з каталогу.
 *
 * ⛔ `sum`/`avg` — члени закритого переліку сервера (`ReportLayout.Functions`),
 * а не текст інтерфейсу: вони не перекладаються й нічого не пояснюють тому, хто
 * не читав коду. Невідома функція теж не показується сирою — інакше нова
 * функція сервера мовчки поїхала б на екран англійським кодом.
 *
 * ⚠ Виклики `t()` розписані літералами навмисно: сторож
 * `Кожен_рядок_якого_просить_клієнт_є_в_каталозі` (`EndpointCoverageTests`)
 * бачить лише `t('ключ')`, і таблиця ключів у змінній сховала б їх від нього.
 */
function totalFnLabel(fn: string): string {
  switch (fn) {
    case 'sum':
      return t('snapshots.rowsFnSum');
    case CountFn:
      return t('snapshots.rowsFnCount');
    case 'avg':
      return t('snapshots.rowsFnAvg');
    case 'min':
      return t('snapshots.rowsFnMin');
    case 'max':
      return t('snapshots.rowsFnMax');
    default:
      return t('snapshots.rowsFnOther');
  }
}

/** Значення підсумку своїм типом. */
function totalValueNode(total: SnapshotTotal, column: SnapshotColumn): ReactNode {
  /*
   * ⚠ `count` — це КІЛЬКІСТЬ рядків, тобто число, хоч би що стояло в самій
   * колонці (те саме рішення, що в книзі). Показати кількість датою означало б
   * зробити з трьох рядків 1970 рік.
   */
  if (total.fn === CountFn) return cellText(total.value, '', NumberKind);

  return valueNode(total.value, column);
}

/** Колонка за кодом; `undefined` — колонки з таким кодом у зрізі немає. */
function columnOf(
  columns: readonly SnapshotColumn[],
  code: string,
): SnapshotColumn | undefined {
  return columns.find((column) => column.code === code);
}

/**
 * Значення заголовка групи чи підсумку.
 *
 * ⛔ Дата — лише `<Timestamp>` (`D15-09`): сирий рядок сервера це
 * `2026-03-31T00:00:00`, у якому людині потрібні три числа з дев'яти.
 */
function valueNode(value: unknown, column: SnapshotColumn | undefined): ReactNode {
  if (column?.kind === DateKind && typeof value === 'string') {
    return <Timestamp value={value} dateOnly />;
  }

  return cellText(value, column?.code ?? '', column?.kind ?? '');
}

/**
 * Ідентифікатор за кодом колонки: число лише за типом.
 *
 * ⚠ Роздільники розрядів зробили б із `202603` «202 603» — тобто показали б
 * ключ періоду як величину.
 */
const IdentifierCode = /Id$|^PeriodKey$/;

/**
 * Скільки знаків дробової частини показуємо. Рішення `D15-09`, не нове.
 *
 * ⚠ Одне число на обидві дороги значення: `formatNumber` для справжнього
 * `number` і `formatDecimal` для рядка, який через `number` вести не можна.
 * Два різні числа тут означали б, що та сама величина виглядає по-різному
 * залежно від того, чи вліз її масштаб у `double`.
 *
 * ⚠ Стеля зрізу звітності НЕ збігається зі стелею переліку
 * (`DataTable.CellFractionCeiling`, три знаки — дефолт `Intl`). Розбіжність
 * існувала й до зведення копій в одне місце; тепер вона видима аргументом, а не
 * схована в другій копії форматувальника.
 */
const CellFractionCeiling = 10;

const CellNumberOptions: Intl.NumberFormatOptions = {
  maximumFractionDigits: CellFractionCeiling,
};

/**
 * Значення комірки текстом.
 *
 * ⛔ Десяткове приходить РЯДКОМ (`e470777a`), тож `typeof value === 'number'`
 * самого по собі вже не досить: без рядкової гілки числова колонка зрізу
 * показувала б сире `1234.5000000000` — без роздільників розрядів і з хвостом
 * нулів масштабу колонки.
 *
 * ⚠ Рядкову гілку вмикає `kind` колонки (`SnapshotColumn.kind`), а не «схоже на
 * число»: текстова колонка цілком законно несе `'007'`, і розпізнавання за
 * виглядом показало б його сімкою. Той самий `kind` уже вирішує вирівнювання
 * клітинки праворуч, тож другого джерела правди тут не заводиться.
 */
function cellText(value: unknown, code: string, kind: string): string {
  if (value === null || value === undefined) return '—';

  if (IdentifierCode.test(code)) return String(value);

  if (typeof value === 'number') return formatNumber(value, CellNumberOptions);

  if (kind === NumberKind && typeof value === 'string') {
    /*
     * ⛔ `formatNumber(Number(value), …)` тут був би не скороченням, а втратою:
     * `decimal(28,16)` не вміщається в IEEE-754, і сервер перевів його в рядок
     * (`e470777a`) рівно щоб цього не сталося. `formatDecimal` веде значення до
     * екрана рядком — і живе в `shared/format`, а не копією тут.
     */
    const shown = formatDecimal(value, undefined, CellFractionCeiling);

    if (shown !== null) return shown;
  }

  return String(value);
}
