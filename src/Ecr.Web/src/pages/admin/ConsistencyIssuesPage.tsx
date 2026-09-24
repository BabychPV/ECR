import { useState, type JSX } from 'react';
import { Badge, Button, Checkbox, Group, Table, Text, TextInput } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { ConsistencyIssue, ConsistencyIssuePage } from '@/api/types';
import {
  ConsistencyIssuesKey,
  RunConsistencyPermission,
  useConsistencyRun,
  type ConsistencyRun,
} from '@/features/jobs/useConsistencyRun';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { ReasonModal } from '@/shared/ui/ReasonModal';
import { Timestamp } from '@/shared/ui/Timestamp';
import { useDebouncedFilter, useFilterCursor } from '@/shared/ui/useDebouncedFilter';
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

  const openOnly = showResolved !== '1';
  const rule = ruleCode ?? '';
  // ⛔ Код правила набирається з клавіатури — у запит після паузи, а не на
  // кожну літеру (той самий дефект, що в журналі змін, `useDebouncedFilter`).
  const appliedRule = useDebouncedFilter(rule);
  // ⛔ Курсор — від застосованого фільтра, не від сирого поля (`useFilterCursor`).
  const [cursor, setCursor] = useFilterCursor(`${appliedRule}|${String(openOnly)}`);

  // ⚠ Дія «перевірити зараз» — лише з правом, яке вимагає сам ендпоінт
  // (`System.RunJob`), а не тим, яким відкрито екран: інакше кнопка обіцяла б
  // дію, на яку сервер гарантовано відповість `403`.
  const session = useSession();
  const runs = can(session.data, RunConsistencyPermission);
  const [asking, setAsking] = useState(false);
  const run = useConsistencyRun();

  const issues = useQuery({
    queryKey: [...ConsistencyIssuesKey, appliedRule, openOnly, cursor],
    queryFn: () =>
      apiFetch<ConsistencyIssuePage>(
        `/api/v1/consistency/issues?limit=100&openOnly=${String(openOnly)}` +
          (appliedRule.length === 0 ? '' : `&ruleCode=${encodeURIComponent(appliedRule)}`) +
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
              }}
            />
            <Checkbox
              label={t('consistency.openOnly')}
              checked={openOnly}
              onChange={(event) => {
                setShowResolved(event.currentTarget.checked ? null : '1');
              }}
            />
            {runs && (
              <Button
                size="xs"
                loading={run.isStarting}
                disabled={run.outcome === 'running'}
                onClick={() => setAsking(true)}
              >
                {t('consistency.runNow')}
              </Button>
            )}
          </Group>
        }
      />

      {/* ⛔ `L10`: відмова постановки — видима, з кодом і текстом сервера, а не
          тост, що зникає, і не «нічого не сталося». */}
      <ErrorAlert error={run.startError} />
      <RunStatus run={run} />

      <ReasonModal
        opened={asking}
        title={t('consistency.runNow')}
        label={t('workflow.reason')}
        description={t('consistency.runHint')}
        confirmLabel={t('consistency.runNow')}
        isPending={run.isStarting}
        onConfirm={(reason) => {
          setAsking(false);
          run.start(reason);
        }}
        onClose={() => setAsking(false)}
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
                    {/* ⚠ Момент знахідки — з годиною, і саме він відповідає на
                        «якого прогону це рядок»: нічна перевірка ходить раз на
                        добу, але ручний перезапуск дає кілька за день. Точне
                        значення лишається в `dateTime`/`title`. */}
                    <Table.Td>
                      <Timestamp value={issue.detectedAt} />
                    </Table.Td>
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
 * Стан ручного прогону — таким, яким його каже сервер.
 *
 * ⛔ Чотири стани, і `unknown` — окремий: «стан прочитати не вдалося» (брак
 * `System.ViewHealth` на `GET /jobs/{id}`) не читається як «виконується».
 * Інакше рядок «перевіряється…» висів би вічно над задачею, стан якої просто
 * не показують.
 */
function RunStatus({ run }: { run: ConsistencyRun }): JSX.Element | null {
  if (run.outcome === null) return null;

  // ⚠ Ключі літералами, не складені з рядка: сторожі каталогу шукають саме
  // виклик `t('…')` і складеного ключа не побачили б.
  const tone = {
    running: { color: 'gray', label: t('consistency.runRunning') },
    succeeded: { color: 'statusSuccess', label: t('consistency.runSucceeded') },
    failed: { color: 'statusError', label: t('consistency.runFailed') },
    unknown: { color: 'statusWarning', label: t('consistency.runUnknown') },
  }[run.outcome];

  return (
    <Group gap="xs" mb="xs" role="status" data-outcome={run.outcome}>
      <Badge size="sm" variant="light" color={tone.color}>
        {tone.label}
      </Badge>
      {/* ⚠ Сервер відповів «перевірка вже йде» і назвав задачу: стежимо за нею,
          і людина має знати, що це не її натискання поставило прогін. */}
      {run.joined && (
        <Text size="sm" c="dimmed">
          {t('consistency.runJoined')}
        </Text>
      )}
      {run.failure !== null && <Text size="sm">{run.failure}</Text>}
    </Group>
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
