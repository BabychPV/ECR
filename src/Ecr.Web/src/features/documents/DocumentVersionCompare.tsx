import { useState, type JSX } from 'react';
import { Badge, Button, Group, Select, Stack, Table, Text, Title } from '@mantine/core';
import {
  compareCellText,
  groupCompareByTable,
  isCompareEmpty,
  type TableDiff,
} from '@/features/documents/documentCompareGroups';
import {
  CurrentState,
  useDocumentCompare,
  useDocumentVersions,
  type CompareTarget,
  type DocumentVersion,
  type RowChange,
} from '@/features/documents/documentVersionsApi';
import { formatDateTime } from '@/shared/format';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';

/** Чий документ і за який період порівнюємо. */
export interface DocumentVersionCompareProps {
  /** Документ. */
  readonly documentId: number;

  /**
   * Період.
   *
   * ⚠ Частина АДРЕСИ даних, а не фільтр показу (`R-A6`): версії належать
   * періоду, і сервер відмовляє порівнювати версії різних періодів
   * (`ECR-DOC-0422 .comparePeriods`).
   */
  readonly periodKey: number;
}

/** Вибір людини: дві версії в межах одного періоду. */
interface ComparePick {
  readonly periodKey: number;

  /** Версія-джерело; `null` — ще не обрано. */
  readonly from: string | null;

  /** Версія-ціль або {@link CurrentState}. */
  readonly to: string;
}

/** Замовлене порівняння — те, що вже поїхало на сервер. */
interface CompareRequest {
  readonly periodKey: number;
  readonly from: number;
  readonly to: CompareTarget;
}

/**
 * Порівняння двох версій документа (`ФВ-5.22`).
 *
 * ⚠ Згорнутий за замовчуванням, і до розгортання не робить ЖОДНОГО запиту
 * (`L2`, той самий прийом, що `WorkflowHistory`): сторінка документа вже робить
 * чотири запити на відкриття, а порівнювати версії приходить одиниця з тих, хто
 * її відкрив.
 *
 * ⛔ Порівняння запускає ЛЮДИНА, натиснувши кнопку. Автоматичний прогін «двох
 * найновіших» відразу після розгортання показував би різницю, про яку ніхто не
 * просив, — і робив би це на кожному відкритті блоку.
 *
 * ⛔ Стелі (200 версій, 1000 записів на перелік) ставить СЕРВЕР. Клієнт не
 * додає до запиту нічого понад контракт: параметр, доданий тут «щоб не тягнути
 * зайвого», змінив би поведінку, описану контрактом, і розійшовся б із нею
 * мовчки.
 */
export function DocumentVersionCompare({
  documentId,
  periodKey,
}: DocumentVersionCompareProps): JSX.Element {
  const [opened, setOpened] = useState(false);
  const versions = useDocumentVersions(documentId, periodKey, opened);

  /*
   * ⛔ Вибір і замовлення несуть ПЕРІОД, на якому їх зробили, — і показуються
   * лише під ним. Це той самий дефект, що вже ловили на цьому екрані з
   * результатом перевірки (`DocumentPage`, аудит §10.7): оператор порівнює
   * версії періоду 202401, міняє період у шапці — перелік версій коректно
   * перезапитується, а таблиця різниці лишається під періодом, версій якого в
   * ній немає. Скидати ефектом на зміну періоду тут не можна з тієї ж причини,
   * що й там: `periodKey` на момент ВІДПОВІДІ вже може бути іншим, ніж на
   * момент запиту.
   */
  const [pick, setPick] = useState<ComparePick>({ periodKey, from: null, to: CurrentState });
  const picked: ComparePick =
    pick.periodKey === periodKey ? pick : { periodKey, from: null, to: CurrentState };

  const [asked, setAsked] = useState<CompareRequest | null>(null);
  const request = asked !== null && asked.periodKey === periodKey ? asked : null;

  const compare = useDocumentCompare(documentId, request?.from ?? null, request?.to ?? CurrentState);

  const available = versions.data ?? [];
  const options = available.map((version) => ({
    value: String(version.versionId),
    label: versionLabel(version),
  }));

  /*
   * ⛔ Відмова й порожня різниця розведені навмисно (`L10`). «Версії однакові» —
   * це твердження про ЗВІРЕНІ дані; сказати його тому, у кого запит упав, означає
   * запевнити, що подання не змінювалося, хоча насправді його не порівнювали.
   *
   * ⚠ Тому ознака відмови перевіряється ОКРЕМО від наявності даних: React Query
   * тримає попередню відповідь, і невдале ПОВТОРНЕ читання лишає на екрані те,
   * що вже знали, — але права оголосити версії однаковими це не дає.
   */
  const failed = compare.error !== null;
  const shown = request === null ? undefined : compare.data;

  return (
    <Stack gap="xs" align="flex-start" data-testid="document-version-compare">
      <Button
        size="xs"
        variant="subtle"
        aria-expanded={opened}
        onClick={() => setOpened((value) => !value)}
      >
        {t('document.compare')}
      </Button>

      {opened && versions.error !== null && (
        <ErrorAlert error={versions.error} onRetry={() => void versions.refetch()} />
      )}

      {/* ⛔ Порожній перелік — це «документ жодного разу не подавали», а не
          несправність: версія існує лише як зріз подання. Порожні поля вибору
          без пояснення читалися б як не завантажені. */}
      {opened && versions.data?.length === 0 && (
        <Text size="sm" c="dimmed" data-testid="document-compare-no-versions">
          {t('document.compareNoVersions')}
        </Text>
      )}

      {opened && available.length > 0 && (
        <Group gap="xs" align="flex-end">
          <Select
            size="xs"
            miw={220}
            label={t('document.compareFrom')}
            data={options}
            value={picked.from}
            onChange={(value) => setPick({ periodKey, from: value, to: picked.to })}
          />

          {/* ⚠ «Поточний стан» — окрема ПЕРША опція, а не порожнє значення:
              порівняння з тим, що в документі зараз, — найчастіше з питань, і
              воно не є версією. Сервер приймає його рядком `current`. */}
          <Select
            size="xs"
            miw={220}
            label={t('document.compareTo')}
            data={[{ value: CurrentState, label: t('document.compareCurrent') }, ...options]}
            value={picked.to}
            onChange={(value) =>
              setPick({ periodKey, from: picked.from, to: value ?? CurrentState })
            }
          />

          <Button
            size="xs"
            variant="default"
            loading={compare.isFetching}
            disabled={picked.from === null}
            onClick={() => {
              if (picked.from === null) return;

              setAsked({
                periodKey,
                from: Number(picked.from),
                to: picked.to === CurrentState ? CurrentState : Number(picked.to),
              });
            }}
          >
            {t('document.compareRun')}
          </Button>
        </Group>
      )}

      {opened && request !== null && failed && (
        <ErrorAlert error={compare.error} onRetry={() => void compare.refetch()} />
      )}

      {/* ⛔ Банер, а не мовчання. `truncated` означає, що сервер віддав лише
          частину розбіжностей, і саме в невидимій частині може лежати та, заради
          якої порівняння й запустили: показана частина без цього попередження
          відповідає на питання «що змінилося» неправдою. */}
      {opened && shown?.truncated === true && (
        <Banner
          tone="warning"
          testId="document-compare-truncated"
          title={t('document.compareTruncatedTitle')}
          text={t('document.compareTruncatedHint', {
            changes: shown.changes.length,
            added: shown.addedRows.length,
            removed: shown.removedRows.length,
          })}
        />
      )}

      {opened && shown !== undefined && !failed && isCompareEmpty(shown) && (
        <Text size="sm" data-testid="document-compare-identical">
          {t('document.compareIdentical')}
        </Text>
      )}

      {/* ⚠ `data-allow-dotted` — на ДАНИХ, а не на сторінці (`test/keyLikeText.ts`):
          ключ рядка, код колонки й саме значення комірки цілком законно містять
          крапки, і сторож технічних ключів інакше вважав би їх написами, яких
          забули перекласти. */}
      {opened && shown !== undefined && !isCompareEmpty(shown) && (
        <Stack gap="md" data-allow-dotted data-testid="document-compare-result">
          {groupCompareByTable(shown).map((diff) => (
            <TableDiffBlock key={diff.tableCode} diff={diff} />
          ))}
        </Stack>
      )}
    </Stack>
  );
}

/** Різниця однієї таблиці: зміни, потім додані рядки, потім видалені. */
function TableDiffBlock({ diff }: { readonly diff: TableDiff }): JSX.Element {
  return (
    <Stack gap="xs">
      <Title order={4}>{diff.tableCode}</Title>

      {diff.changes.length > 0 && (
        <Table striped data-testid="document-compare-changes">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('document.compareRowKey')}</Table.Th>
              <Table.Th>{t('document.compareColumn')}</Table.Th>
              <Table.Th>{t('document.compareOldValue')}</Table.Th>
              <Table.Th>{t('document.compareNewValue')}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {diff.changes.map((change) => (
              <Table.Tr key={`${change.rowKey}:${change.columnCode}`}>
                <Table.Td>{change.rowKey}</Table.Td>
                <Table.Td>{change.columnCode}</Table.Td>
                {/* ⛔ Значення йдуть ЯК ПРИЙШЛИ — див. `compareCellText`. */}
                <Table.Td>{compareCellText(change.oldValue)}</Table.Td>
                <Table.Td>{compareCellText(change.newValue)}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}

      {/* ⛔ Додані й видалені рядки — ОКРЕМІ блоки з власною позначкою, а не
          рядки таблиці змін. Доданий рядок порівнювати нема з чим: показати
          його зміною «порожньо → значення» означало б сказати, що раніше там
          стояла порожня комірка, хоча раніше там не було рядка. */}
      {diff.addedRows.length > 0 && (
        <RowMarkBlock
          rows={diff.addedRows}
          testId="document-compare-added"
          title={t('document.compareAddedTitle')}
          mark={t('document.compareAdded')}
          tone="statusSuccess"
        />
      )}

      {diff.removedRows.length > 0 && (
        <RowMarkBlock
          rows={diff.removedRows}
          testId="document-compare-removed"
          title={t('document.compareRemovedTitle')}
          mark={t('document.compareRemoved')}
          tone="statusWarning"
        />
      )}
    </Stack>
  );
}

/**
 * Перелік рядків із позначкою «нове» або «видалено».
 *
 * ⚠ Підписи приходять готовими рядками, а не ключами: сторож
 * `Кожен_рядок_якого_просить_клієнт_є_в_каталозі` бачить лише `t('ключ')`
 * літералом, і `t(added ? 'a' : 'b')` сховав би обидва ключі від нього.
 */
function RowMarkBlock({
  rows,
  testId,
  title,
  mark,
  tone,
}: {
  readonly rows: readonly RowChange[];
  readonly testId: string;
  readonly title: string;
  readonly mark: string;
  readonly tone: string;
}): JSX.Element {
  return (
    <Stack gap="xs" data-testid={testId}>
      <Text size="sm" fw={600}>
        {title}
      </Text>
      <Group gap="xs" role="list">
        {rows.map((row) => (
          <Group key={row.rowId} gap="xs" role="listitem">
            <Badge size="xs" variant="light" color={tone}>
              {mark}
            </Badge>
            <Text size="sm">{row.rowKey}</Text>
          </Group>
        ))}
      </Group>
    </Stack>
  );
}

/**
 * Підпис версії у випадному списку: коли подано і хто подав.
 *
 * ⛔ Мить форматується (`D15-09`), а не друкується сирим рядком ISO: у списку,
 * де треба вибрати одну з двохсот, `2026-09-22T08:15:42.1234567Z` не читається
 * зовсім. Компонентом `<Timestamp>` тут не обійтися — `Select` приймає підпис
 * рядком.
 *
 * ⚠ Нерозбірливу мить показуємо ЯК Є: `formatDateTime` повертає на такій
 * порожній рядок, і підпис «· Ivanov» без дати виглядав би як зламаний список,
 * а не як зіпсоване значення сервера.
 */
function versionLabel(version: DocumentVersion): string {
  const at = formatDateTime(version.submittedAt);

  return `${at === '' ? version.submittedAt : at} · ${version.submittedBy}`;
}
