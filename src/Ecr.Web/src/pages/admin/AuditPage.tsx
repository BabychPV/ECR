import { useState, type JSX } from 'react';
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
import { cellChangeOrigins, isSingleCell, useCellChanges } from '@/features/audit/api';
import { StructureChangesPanel } from '@/features/audit/StructureChangesPanel';
import { StructureExportButton } from '@/features/audit/StructureExportButton';
import { Timestamp } from '@/shared/ui/Timestamp';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { useUrlNumber, useUrlParamsSetter, useUrlState } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';

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
  const [cursor, setCursor] = useState<string | null>(null);

  const fromDate = from ?? isoDaysAgo(7);
  const toDate = to ?? isoDaysAgo(0);

  const filter = {
    from: fromDate,
    to: toDate,
    documentId,
    rowKey,
    columnDefId,
    author,
    origin,
    lateOnly: lateOnly === 'true',
    limit: 100,
    cursor,
  };

  // ⚠ `BE-16`: друга вкладка — журнал структурних змін. Вкладка живе в адресі
  // (`?view=structure`), бо саме адресу людина надсилає колезі. Журнал комірок
  // на ній НЕ запитується: зайвий запит по партиціях заради невидимої таблиці.
  const structure = view === 'structure';
  const changes = useCellChanges(filter, !structure);

  /**
   * ⚠ Курсор скидається на КОЖНУ зміну фільтра — він позначає позицію в
   * конкретній видачі, і сторінка 5 попереднього фільтра не є сторінкою 5
   * нового. Без цього зміна фільтра давала б порожню сторінку замість перших
   * результатів, і це читалося б як «нічого не знайдено».
   */
  const single = isSingleCell(filter);

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
              value={fromDate}
              onChange={(event) => {
                setFrom(event.currentTarget.value);
                setCursor(null);
              }}
            />
            <TextInput
              size="xs"
              // eslint-disable-next-line no-restricted-syntax -- D15-09, борг №2/8: див. коментар вище
              type="date"
              label={t('audit.to')}
              value={toDate}
              onChange={(event) => {
                setTo(event.currentTarget.value);
                setCursor(null);
              }}
            />
            <NumberInput
              size="xs"
              miw={140}
              // ⚠ На вкладці структурних змін документа немає: мертвий фільтр
              // читався б як «за цим документом змін не було».
              display={structure ? 'none' : undefined}
              label={t('audit.document')}
              description={t('audit.documentHint')}
              value={documentId ?? ''}
              onChange={(value) => {
                setDocumentId(typeof value === 'number' ? value : null);
                setCursor(null);
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
      <Group gap="xs" align="end" mb="md" wrap="wrap">
        <NumberInput
          size="xs"
          miw={140}
          label={t('audit.author')}
          description={t('audit.authorHint')}
          value={author ?? ''}
          onChange={(value) => {
            setAuthor(typeof value === 'number' ? value : null);
            setCursor(null);
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
            setCursor(null);
          }}
        />
        <TextInput
          size="xs"
          miw={120}
          label={t('audit.rowKey')}
          description={t('audit.cellHint')}
          value={rowKey ?? ''}
          onChange={(event) => {
            setRowKey(event.currentTarget.value);
            setCursor(null);
          }}
        />
        <NumberInput
          size="xs"
          miw={120}
          label={t('audit.columnDefId')}
          value={columnDefId ?? ''}
          onChange={(value) => {
            setColumnDefId(typeof value === 'number' ? value : null);
            setCursor(null);
          }}
        />
        <Checkbox
          mb="xs"
          label={t('audit.lateOnly')}
          checked={lateOnly === 'true'}
          onChange={(event) => {
            setLateOnly(event.currentTarget.checked ? 'true' : null);
            setCursor(null);
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
            setCursor(null);
          }}
        >
          {t('audit.reset')}
        </Button>
      </Group>

      <AsyncBoundary<CellChangePage>
        isPending={changes.isPending}
        error={changes.error}
        data={changes.data}
        isEmpty={(page) => page.items.length === 0}
        emptyTitle={t('audit.empty')}
        emptyHint={t('audit.emptyHint')}
        skeleton="table"
        onRetry={() => void changes.refetch()}
      >
        {(page) => (
          <>
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
                {page.items.map((change, index) => (
                  <Table.Tr key={`${change.documentId}:${change.rowKey}:${change.columnDefId}:${index}`}>
                    <Table.Td>
                      {/* ⛔ Саме `Timestamp`, а не сирий рядок, і аргумент
                          «журнал — доказ, доказ мусить бути однозначним»
                          цьому НЕ суперечить: точне значення нікуди не
                          дівається, воно лишається в `dateTime`/`title`
                          (`<time datetime="2026-09-19T18:51:58.275Z">`).
                          Читабельним стає лише те, що бачить око — а саме цю
                          колонку оператор читає рядок за рядком, шукаючи «хто
                          і коли», не звіряючи мілісекунди. */}
                      <Timestamp value={change.changedAt} />
                      {/* ⚠ Пізня правка — та, що зроблена в пільговому строку
                          після кінця періоду (`D-70`). В аудиті вона виглядає
                          інакше саме тому, що пояснювати доводиться саме її. */}
                      {change.isLateEdit && (
                        <Badge ml="xs" size="xs" color="statusWarning" variant="light">
                          {t('audit.late')}
                        </Badge>
                      )}
                    </Table.Td>
                    <Table.Td>{change.changedByUserId}</Table.Td>
                    <Table.Td>{change.documentId}</Table.Td>
                    <Table.Td>{change.periodKey}</Table.Td>
                    <Table.Td>
                      <Text size="xs">
                        {change.rowKey} · {change.columnDefId}
                      </Text>
                    </Table.Td>
                    <Table.Td>{change.oldValue ?? '—'}</Table.Td>
                    <Table.Td>{change.newValue ?? '—'}</Table.Td>
                    <Table.Td>
                      {/* ⚠ Походження відрізняє руку людини від збору з
                          джерела: «звідки це число» — питання, з якого
                          починається кожне розслідування розбіжності. */}
                      <Badge size="sm" variant="light">
                        {change.origin}
                      </Badge>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>

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
