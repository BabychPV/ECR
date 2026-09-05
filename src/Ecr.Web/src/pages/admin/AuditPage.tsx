import { useState, type JSX } from 'react';
import { Badge, Button, Group, NumberInput, Table, Text, TextInput } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { CellChangePage } from '@/api/types';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { useUrlNumber, useUrlState } from '@/shared/ui/useUrlState';
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
 */
export function AuditPage(): JSX.Element {
  const [from, setFrom] = useUrlState('from');
  const [to, setTo] = useUrlState('to');
  const [documentId, setDocumentId] = useUrlNumber('documentId');
  const [cursor, setCursor] = useState<string | null>(null);

  const fromDate = from ?? isoDaysAgo(7);
  const toDate = to ?? isoDaysAgo(0);

  const changes = useQuery({
    queryKey: ['audit-cells', fromDate, toDate, documentId, cursor],
    queryFn: () =>
      apiFetch<CellChangePage>(
        `/api/v1/audit/cells?from=${fromDate}&to=${toDate}&limit=100` +
          (documentId === null ? '' : `&documentId=${documentId}`) +
          (cursor === null ? '' : `&cursor=${encodeURIComponent(cursor)}`),
      ),
  });

  return (
    <>
      <PageHeader
        title={t('audit.title')}
        actions={
          <Group gap="xs" align="end">
            <TextInput
              size="xs"
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
                      {change.changedAt}
                      {/* ⚠ Пізня правка — та, що зроблена в пільговому строку
                          після кінця періоду (`D-70`). В аудиті вона виглядає
                          інакше саме тому, що пояснювати доводиться саме її. */}
                      {change.isLateEdit && (
                        <Badge ml="xs" size="xs" color="orange" variant="light">
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
