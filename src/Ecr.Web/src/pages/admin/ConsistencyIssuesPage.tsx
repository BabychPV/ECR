import { useState, type JSX } from 'react';
import { Badge, Button, Checkbox, Group, Table, Text, TextInput } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { ConsistencyIssue, ConsistencyIssuePage } from '@/api/types';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { useUrlState } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';

/**
 * Журнал знахідок нічної перевірки узгодженості (`aud.ConsistencyIssue`).
 *
 * ⛔ Екрана не було, і це не «не встигли намалювати»: рядків
 * `aud.ConsistencyIssue` не читав ЖОДЕН ендпоінт і жоден екран. Видимою була
 * тільки кількість — лічильник OpenTelemetry `ecr.consistency.issues` із
 * міткою `kind`. Тобто адміністратор дізнавався «є 12 знахідок `BROKEN_FK`» і
 * не мав жодного способу дізнатися, ЯКІ САМЕ рядки зачеплені, окрім запиту до
 * бази руками. Лічильник без переліку відповідає на «скільки», а діяти можна
 * лише за відповіддю на «де».
 *
 * ⚠ Текст знахідки (`message`) — УКРАЇНСЬКИЙ і показується як є. Його пише
 * `ConsistencyCheckJob`, а механізм каталогу рядків існує для відмов API
 * (`err.*`), не для цього журналу. Рішення свідоме: підпис колонки
 * (`consistency.messageLanguage`) каже це прямо на екрані, щоб український
 * рядок в англійському інтерфейсі не читався як дефект локалізації.
 *
 * ⚠ Фільтр «лише нерозв'язані» увімкнений ЗА ЗАМОВЧУВАННЯМ. Журнал накопичує
 * знахідки від кожного нічного прогону, і питання адміністратора майже завжди
 * «що зламано ЗАРАЗ», а не «що колись знаходили».
 */
export function ConsistencyIssuesPage(): JSX.Element {
  const [ruleCode, setRuleCode] = useUrlState('ruleCode');
  const [showResolved, setShowResolved] = useUrlState('showResolved');
  const [cursor, setCursor] = useState<string | null>(null);

  const openOnly = showResolved !== '1';
  const rule = ruleCode ?? '';

  const issues = useQuery({
    queryKey: ['consistency-issues', rule, openOnly, cursor],
    queryFn: () =>
      apiFetch<ConsistencyIssuePage>(
        `/api/v1/consistency/issues?limit=100&openOnly=${String(openOnly)}` +
          (rule.length === 0 ? '' : `&ruleCode=${encodeURIComponent(rule)}`) +
          (cursor === null ? '' : `&cursor=${encodeURIComponent(cursor)}`),
      ),
  });

  return (
    <>
      <PageHeader
        title={t('consistency.title')}
        actions={
          <Group gap="xs" align="end">
            <TextInput
              size="xs"
              miw={220}
              label={t('consistency.rule')}
              description={t('consistency.ruleHint')}
              value={rule}
              onChange={(event) => {
                setRuleCode(event.currentTarget.value);
                setCursor(null);
              }}
            />
            <Checkbox
              label={t('consistency.openOnly')}
              checked={openOnly}
              onChange={(event) => {
                setShowResolved(event.currentTarget.checked ? null : '1');
                setCursor(null);
              }}
            />
          </Group>
        }
      />

      <AsyncBoundary<ConsistencyIssuePage>
        isPending={issues.isPending}
        error={issues.error}
        data={issues.data}
        isEmpty={(page) => page.items.length === 0}
        emptyTitle={t('consistency.empty')}
        emptyHint={t('consistency.emptyHint')}
        skeleton="table"
        onRetry={() => void issues.refetch()}
      >
        {(page) => (
          <>
            <Table striped className="ecr-sticky-head">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t('consistency.when')}</Table.Th>
                  <Table.Th>{t('consistency.severity')}</Table.Th>
                  <Table.Th>{t('consistency.rule')}</Table.Th>
                  <Table.Th>{t('consistency.entity')}</Table.Th>
                  <Table.Th>{t('consistency.what')}</Table.Th>
                  <Table.Th>{t('consistency.state')}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {page.items.map((issue) => (
                  <Table.Tr key={issue.id}>
                    <Table.Td>{issue.detectedAt}</Table.Td>
                    <Table.Td>
                      <Badge size="sm" variant="light" color={severityColor(issue.severity)}>
                        {severityLabel(issue.severity)}
                      </Badge>
                    </Table.Td>
                    {/* ⚠ `data-allow-dotted`: код правила (`ARCHIVE_CHECKSUM`) і
                        тип сутності (`doc.CellValue`) — це ДАНІ з журналу, а не
                        ключі каталогу. Виняток стоїть на самих комірках, а не
                        на сторінці: сторож, вимкнений на сторінці, — це сторож,
                        якого немає. */}
                    <Table.Td data-allow-dotted>
                      <Text size="xs">{issue.ruleCode}</Text>
                    </Table.Td>
                    <Table.Td data-allow-dotted>
                      <Text size="xs">
                        {issue.entityType ?? '—'}
                        {issue.entityId === null ? '' : ` · ${String(issue.entityId)}`}
                      </Text>
                    </Table.Td>
                    {/* ⚠ Головна колонка екрана: саме вона відповідає на «де
                        саме», якого не давав лічильник. Мова тексту — див.
                        підпис під таблицею. */}
                    <Table.Td>{issue.message}</Table.Td>
                    <Table.Td>
                      {issue.resolvedAt === null ? (
                        <Badge size="sm" variant="light" color="statusWarning">
                          {t('consistency.open')}
                        </Badge>
                      ) : (
                        <Badge size="sm" variant="light" color="statusSuccess">
                          {t('consistency.resolved')}
                        </Badge>
                      )}
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>

            <Text size="xs" c="dimmed" mt="xs">
              {t('consistency.messageLanguage')}
            </Text>

            {/* ⚠ Курсорна пагінація: журнал росте від кожного нічного прогону,
                і стеля одного проходу — тисяча знахідок. */}
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
 * Підпис ваги знахідки.
 *
 * ⚠ Голе число (1/2/3) на екрані нічого не означає: вагу задає задача, і
 * шкала живе в її коді, а не в голові адміністратора.
 */
function severityLabel(severity: ConsistencyIssue['severity']): string {
  switch (severity) {
    case 1:
      return t('consistency.severityInfo');
    case 2:
      return t('consistency.severityWarning');
    default:
      return t('consistency.severityError');
  }
}

/** Колір ваги: інформація нейтральна, попередження жовте, помилка червона. */
function severityColor(severity: ConsistencyIssue['severity']): string {
  switch (severity) {
    case 1:
      return 'gray';
    case 2:
      return 'statusWarning';
    default:
      return 'statusError';
  }
}
