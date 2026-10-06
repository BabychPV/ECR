import { lazy, Suspense, useState, type JSX } from 'react';
import { Badge, Button, Checkbox, Group, Text, UnstyledButton } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { ConsistencyIssue, ConsistencyIssuePage } from '@/api/types';
import type { components } from '@/api/schema';
import {
  ConsistencyIssuesKey,
  RunConsistencyPermission,
  useConsistencyRun,
  type ConsistencyRun,
} from '@/features/jobs/useConsistencyRun';
import { can, useSession } from '@/shared/session/useSession';
import { DataTable, type DataTableColumn } from '@/shared/ui/DataTable';
import { useDetailPanel } from '@/shared/ui/DetailDrawer';
import { ErrorAlert, TechnicalDetails } from '@/shared/ui/ErrorAlert';
import { FilterBar } from '@/shared/ui/FilterBar';
import { ListPage } from '@/shared/ui/ListPage';
import { ReasonModal } from '@/shared/ui/ReasonModal';
import type { StatItem, StatStripItems } from '@/shared/ui/StatStrip';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';
import { TwoLine } from '@/shared/ui/TwoLine';
import { useDebouncedFilter, useFilterCursor } from '@/shared/ui/useDebouncedFilter';
import { useUrlState } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';

/** Шторка знахідки — лінивим чанком: закрита за замовчуванням (`L2`). */
const ConsistencyIssueDetail = lazy(() => import('@/features/consistency/ConsistencyIssueDetail'));

/** Загальні лічильники журналу (`GET /consistency/summary`). */
type ConsistencySummary = components['schemas']['ConsistencySummary'];

/** Вага в адресі (`?severity=Error`) → код сервера (`ValidationSeverity`). */
const SeverityCodes: Readonly<Record<string, number>> = { Info: 1, Warning: 2, Error: 3 };

/** Значення `?panel=` шторки знахідки. */
export function issuePanelId(issueId: number): string {
  return `issue-${String(issueId)}`;
}

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
 * ⚠ UI-20 (макет `screens-ops.js` `/admin/consistency`, KIT.md §3): шаблон
 * переліку — пояснення під заголовком, головна дія «Run check now», смуга за
 * вагою, рядок фільтрів (`FilterBar`), таблиця, шторка `?panel=issue-<id>`.
 *
 * ⚠ Фільтр «лише нерозв'язані» увімкнений ЗА ЗАМОВЧУВАННЯМ. Журнал накопичує
 * знахідки від кожного нічного прогону, і питання адміністратора майже завжди
 * «що зламано ЗАРАЗ», а не «що колись знаходили».
 */
export function ConsistencyIssuesPage(): JSX.Element {
  const [ruleCode] = useUrlState('ruleCode');
  const [showResolved, setShowResolved] = useUrlState('showResolved');
  const [severity, setSeverity] = useUrlState('severity');
  const [panel, setPanel] = useDetailPanel();

  const openOnly = showResolved !== '1';
  const rule = ruleCode ?? '';
  // ⛔ Код правила набирається з клавіатури — у запит після паузи, а не на
  // кожну літеру (той самий дефект, що в журналі змін, `useDebouncedFilter`).
  // Саме поле (з власною чернеткою, `useFieldDraft`) — пошук `FilterBar`.
  const appliedRule = useDebouncedFilter(rule);
  // ⛔ Курсор — від застосованого фільтра, не від сирого поля (`useFilterCursor`).
  // ⛔ Курсор — від застосованого фільтра, не від сирого поля (`useFilterCursor`).
  // Вага — теж фільтр СЕРВЕРА (LS-E), тож входить у ключ курсора.
  const [cursor, setCursor] = useFilterCursor(`${appliedRule}|${String(openOnly)}|${severity ?? ''}`);

  // ⚠ Дія «перевірити зараз» — лише з правом, яке вимагає сам ендпоінт
  // (`System.RunJob`), а не тим, яким відкрито екран: інакше кнопка обіцяла б
  // дію, на яку сервер гарантовано відповість `403`.
  const session = useSession();
  const runs = can(session.data, RunConsistencyPermission);
  const [asking, setAsking] = useState(false);
  const run = useConsistencyRun();

  const severityCode = severity === null ? null : SeverityCodes[severity];

  const issues = useQuery({
    queryKey: [...ConsistencyIssuesKey, appliedRule, openOnly, severityCode ?? null, cursor],
    queryFn: () =>
      apiFetch<ConsistencyIssuePage>(
        `/api/v1/consistency/issues?limit=100&openOnly=${String(openOnly)}` +
          (appliedRule.length === 0 ? '' : `&ruleCode=${encodeURIComponent(appliedRule)}`) +
          (severityCode === undefined || severityCode === null ? '' : `&severity=${String(severityCode)}`) +
          (cursor === null ? '' : `&cursor=${encodeURIComponent(cursor)}`),
      ),
  });

  /*
   * ⚠ Число в шапці — увесь журнал за станом прапорця (`GET /consistency/summary`),
   * а не сторінка: журнал курсорний.
   */
  const summary = useQuery({
    queryKey: [...ConsistencyIssuesKey, 'summary', openOnly],
    queryFn: () => apiFetch<ConsistencySummary>(`/api/v1/consistency/summary?openOnly=${String(openOnly)}`),
  });

  const page = issues.data;
  const items = page?.items;

  /*
   * ⛔ Смуга за вагою — з `totals` СЕРВЕРА (LS-E): розклад за фільтрами без
   * ваги по всьому журналу, а не лічба завантаженої сотні. До UI-20 + LS-E
   * смуга показувалась лише над повним результатом, бо сервер підсумків не
   * віддавав. Поки відповіді немає — смуги немає (`D15-06`), а не нулі.
   */
  const totals = page?.totals;
  const stats: StatStripItems | undefined =
    totals === undefined || totals === null
      ? undefined
      : ([
          { id: 'Error', label: t('consistency.statErrors'), tone: 'danger', value: totals.errors },
          { id: 'Warning', label: t('consistency.statWarnings'), tone: 'warning', value: totals.warnings },
          { id: 'Info', label: t('consistency.statInfo'), value: totals.info },
        ] satisfies readonly [StatItem, StatItem, StatItem]);

  const severityFilter = severityCode === undefined ? null : severity;
  const shown = items;

  const openIssue = items?.find((issue) => issuePanelId(issue.id) === panel);

  const columns: readonly DataTableColumn<ConsistencyIssue>[] = [
    {
      key: 'severity',
      label: t('consistency.severity'),
      sortValue: (issue) => -issue.severity,
      render: (issue) => (
        <StatusBadge kind="severity" state={severityState(issue.severity)} quiet={issue.resolvedAt !== null} />
      ),
    },
    {
      // ⚠ Головна колонка екрана: саме вона відповідає на «де саме», якого не
      // давав лічильник. Код правила — другим рядком (`KIT.md` §1 п. 7) і
      // кнопкою шторки (клавіатурний шлях; клац по рядку його не має).
      //
      // ⚠ `data-allow-dotted`: код правила — це ДАНІ з журналу, а не ключ
      // каталогу; виняток стоїть на самих даних, не на сторінці.
      key: 'message',
      label: t('consistency.what'),
      sortable: false,
      render: (issue) => (
        <TwoLine
          primary={
            <UnstyledButton
              ta="left"
              fz="sm"
              data-issue-open={issue.id}
              onClick={(event) => {
                event.stopPropagation();
                setPanel(issuePanelId(issue.id));
              }}
            >
              {issue.message}
            </UnstyledButton>
          }
          secondary={<span data-allow-dotted>{issue.ruleCode}</span>}
          mono
        />
      ),
    },
    {
      key: 'entity',
      label: t('consistency.entity'),
      sortValue: (issue) => [issue.entityType ?? '', issue.entityId ?? 0],
      render: (issue) => (
        <Text size="xs" data-allow-dotted>
          {issue.entityType ?? '—'}
          {issue.entityId === null ? '' : ` · ${String(issue.entityId)}`}
        </Text>
      ),
    },
    {
      // ⚠ Момент знахідки — з годиною: нічна перевірка ходить раз на добу,
      // але ручний перезапуск дає кілька за день.
      key: 'detectedAt',
      label: t('consistency.when'),
      render: (issue) => <Timestamp value={issue.detectedAt} />,
    },
    {
      key: 'state',
      label: t('consistency.state'),
      sortValue: (issue) => (issue.resolvedAt === null ? 0 : 1),
      render: (issue) =>
        issue.resolvedAt === null ? (
          <Badge size="sm" variant="light" color="statusWarning">
            {t('consistency.open')}
          </Badge>
        ) : (
          // ⚠ Розв'язане — нейтрально: зелений не вживається для «все гаразд»
          // (KIT.md §1, «Тони»).
          <Badge size="sm" variant="default">
            {t('consistency.resolved')}
          </Badge>
        ),
    },
  ];

  return (
    <ListPage
      header={{
        title: t('consistency.title'),
        count: summary.data?.total,
        meta: t('consistency.description'),
        primary: runs
          ? { label: t('consistency.runNow'), onClick: () => setAsking(true), disabled: run.outcome === 'running' }
          : undefined,
      }}
      stats={
        stats === undefined
          ? undefined
          : { label: t('consistency.statsLabel'), items: stats, active: severityFilter, onSelect: setSeverity }
      }
      filters={
        <FilterBar
          /*
           * ⚠ Пошук — за кодом правила, на СЕРВЕРІ (`ruleCode`): журнал
           * сторінковий, і пошук по завантаженій сотні пропускав би решту.
           * Пояснення — підказкою в порожньому полі.
           */
          search={{ param: 'ruleCode', label: t('consistency.rule'), placeholder: t('consistency.ruleHint') }}
          clearLabel={t('filters.clear')}
          right={
            <Checkbox
              label={t('consistency.openOnly')}
              checked={openOnly}
              onChange={(event) => {
                setShowResolved(event.currentTarget.checked ? null : '1');
              }}
            />
          }
        />
      }
      table={
        <>
          {/* ⛔ `L10`: відмова постановки — видима, з кодом і текстом сервера,
              а не тост, що зникає. */}
          <ErrorAlert error={run.startError} />
          <RunStatus run={run} />

          <DataTable<ConsistencyIssue>
            columns={columns}
            rows={shown}
            rowKey={(issue) => String(issue.id)}
            isPending={issues.isPending}
            error={issues.error}
            onRetry={() => void issues.refetch()}
            emptyTitle={t('consistency.empty')}
            emptyHint={t('consistency.emptyHint')}
            filtered={severityFilter !== null || appliedRule.length > 0 || !openOnly}
            noMatchTitle={t('consistency.noMatch')}
            onClearFilters={severityFilter === null ? undefined : () => setSeverity(null)}
            clearFiltersLabel={t('filters.clear')}
            onRowClick={(issue) => setPanel(issuePanelId(issue.id))}
            selectedKey={openIssue === undefined ? undefined : String(openIssue.id)}
          />
        </>
      }
      detail={
        openIssue === undefined
          ? undefined
          : {
              panelId: issuePanelId(openIssue.id),
              title: openIssue.ruleCode,
              subtitle: `#${String(openIssue.id)}`,
              badge: <StatusBadge kind="severity" state={severityState(openIssue.severity)} />,
              closeLabel: t('common.close'),
              children: (
                <Suspense fallback={<Text size="sm" c="dimmed">{t('common.loading')}</Text>}>
                  <ConsistencyIssueDetail issue={openIssue} />
                </Suspense>
              ),
            }
      }
    >
      {/* Мова тексту знахідки — під таблицею, поки рядки є. */}
      {items !== undefined && items.length > 0 && (
        <Text size="xs" c="dimmed">
          {t('consistency.messageLanguage')}
        </Text>
      )}

      {/* ⚠ Курсорна пагінація: журнал росте від кожного нічного прогону,
          і стеля одного проходу — тисяча знахідок. */}
      {page !== undefined && page.nextCursor !== null && (
        <Group>
          <Button variant="default" onClick={() => setCursor(page.nextCursor)}>
            {t('documents.more')}
          </Button>
        </Group>
      )}

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
    </ListPage>
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
      {/* ⛔ `X-04`: `failure` — сирий `ex.Message` задачі (українською чи
          мовою СУБД), і стояв тут видимим рядком поруч із локалізованим
          «Check failed». Коду помилки хук не віддає, тож людині — бейдж, а
          сирий текст лише згорнутим: екран адміністративний, і саме
          адміністратор його розгортає, розбираючи збій. */}
      {run.failure !== null && run.failure !== '' && (
        <TechnicalDetails label={t('common.technicalDetails')}>{run.failure}</TechnicalDetails>
      )}
    </Group>
  );
}

/**
 * Вага знахідки словником `ValidationSeverity` (`StatusBadge` `severity`).
 *
 * ⚠ Голе число (1/2/3) на екрані нічого не означає: вагу задає задача, і
 * шкала живе в її коді. Невідоме число — `Error`: деградація в бік уваги.
 */
export function severityState(severity: ConsistencyIssue['severity']): 'Info' | 'Warning' | 'Error' {
  switch (severity) {
    case 1:
      return 'Info';
    case 2:
      return 'Warning';
    default:
      return 'Error';
  }
}
