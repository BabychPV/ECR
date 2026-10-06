import type { JSX, ReactNode } from 'react';
import { Box, Button, Group, Skeleton, Stack, Text, Title } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import type { DocumentSummary } from '@/api/types';
import { useTableStatus, type TableStatus } from '@/features/documents/api';
import { documentState, hasSheetStates, sheetLabels, type SheetLabel } from '@/features/documents/documentSheets';
import { useWorkflowHistory, type WorkflowEvent } from '@/features/workflow/api';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { DetailDrawer, useDetailPanel } from '@/shared/ui/DetailDrawer';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { KeyValue } from '@/shared/ui/KeyValue';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';

/** Скільки останніх подій показує блок «Latest changes» (макет — три-чотири). */
export const LatestChangesCount = 3;

interface DocumentQuickLookProps {
  /** Значення `?panel=` — ідентифікатор документа. */
  readonly panelId: string;

  /** Обраний період; без нього станів і заповненості немає (`D-93`). */
  readonly periodKey: number | null;

  /** Рядок уже завантаженої сторінки переліку, якщо документ на ній є. */
  readonly document?: DocumentSummary | undefined;

  /**
   * Чи сторінка переліку вже прочиталась. ⚠ Дочитувати документ окремо можна
   * лише ПІСЛЯ неї: інакше за прямим посиланням `?panel=` кожне відкриття
   * робило б зайвий запит ще до того, як перелік приїхав.
   */
  readonly listReady: boolean;

  readonly projectCode: (projectId: number) => string | undefined;

  readonly documentHref: (documentId: number) => string;

  /** Після закриття (Esc, хрестик, «Close») — щоб сторінка повернула фокус відкривачу. */
  readonly onClose?: (() => void) | undefined;
}

/**
 * Швидкий перегляд документа зі списку (`UI-29`, макет `screens-work.js` →
 * `ctx.panel('*')` на екрані «/», `13-docs-quicklook.png`): аркуші зі станом і
 * заповненістю, хто й коли змінював, три останні переходи; дії «Close» і
 * «Open document».
 *
 * ⛔ Прихований аркуш (P1, безпека): усе — лише з відповідей сервера, які вже
 * відфільтровані за роллю (`sheets`/`sheetStates`, `tables/status`,
 * `workflow/history`). Шторка нічого не рахує «по всіх аркушах»: аркуш, якого
 * немає в `sheets`, не з'являється ні рядком, ні числом.
 *
 * ⚠ «Owner» з макета тут немає: сервер не віддає автора документа, а
 * `modifiedByDisplayName` — це останній, хто змінював. Тому підпис чесний —
 * «Last changed by» (картка `UI-29`).
 *
 * ⚠ Запити — лише відкритої шторки (компонент монтується за `?panel=`), і
 * лише з періодом: без нього аркуші не мають станів, а таблиці — екземплярів.
 */
export function DocumentQuickLook({
  panelId,
  periodKey,
  document: fromList,
  listReady,
  projectCode,
  documentHref,
  onClose,
}: DocumentQuickLookProps): JSX.Element | null {
  const documentId = Number(panelId);
  const valid = Number.isInteger(documentId) && documentId > 0;
  const [, setPanel] = useDetailPanel();
  const navigate = useNavigate();

  // Посилання `?panel=` на документ поза завантаженою сторінкою — дочитати його.
  const fetched = useQuery({
    queryKey: ['document', documentId, periodKey, 'quick-look'],
    queryFn: () =>
      apiFetch<DocumentSummary>(
        `/api/v1/documents/${String(documentId)}` + (periodKey === null ? '' : `?periodKey=${String(periodKey)}`),
      ),
    enabled: valid && listReady && fromList === undefined,
  });

  // ⚠ `?? undefined`: порожня відповідь (`null`) — це «документа немає», а не документ.
  const document = fromList ?? fetched.data ?? undefined;

  if (!valid) return null;

  const name = document === undefined ? '' : localized(document.nameL10n);
  const title = document === undefined ? '' : name.length > 0 ? name : document.businessKey;
  const state = document === undefined ? null : documentState(document);
  const project = document === undefined ? undefined : projectCode(document.projectId);
  const subtitle = document === undefined ? undefined : [name.length > 0 ? document.businessKey : null, project].filter(Boolean).join(' · ');

  return (
    <DetailDrawer
      panelId={panelId}
      title={title}
      subtitle={subtitle}
      badge={state === null ? undefined : <StatusBadge kind="sheet" state={state} />}
      closeLabel={t('documents.quickLookClose')}
      onClose={onClose}
      footer={
        <>
          <Button
            variant="default"
            onClick={() => {
              setPanel(null);
              onClose?.();
            }}
            data-quick-look-close=""
          >
            {t('common.close')}
          </Button>
          {document !== undefined && (
            <Button onClick={() => void navigate(documentHref(document.id))} data-quick-look-open="">
              {t('documents.openDocument')}
            </Button>
          )}
        </>
      }
    >
      {document === undefined ? (
        fetched.error !== null ? (
          <ErrorAlert error={fetched.error} onRetry={() => void fetched.refetch()} />
        ) : (
          <Skeleton height={120} radius="sm" />
        )
      ) : (
        <QuickLookBody document={document} periodKey={periodKey} />
      )}
    </DetailDrawer>
  );
}

function QuickLookBody({
  document,
  periodKey,
}: {
  readonly document: DocumentSummary;
  readonly periodKey: number | null;
}): JSX.Element {
  const withStates = periodKey !== null && hasSheetStates(document);

  return (
    <Stack gap="lg" data-quick-look={document.id}>
      {withStates ? (
        <SheetsSection document={document} periodKey={periodKey} />
      ) : (
        <Text size="sm" c="dimmed" data-quick-look-no-period="">
          {t('documents.quickLookNoPeriod')}
        </Text>
      )}

      <Section title={t('documents.quickLookResponsible')}>
        <KeyValue
          wide
          items={[
            { label: t('documents.lastChangedBy'), value: document.modifiedByDisplayName ?? null },
            {
              label: t('documents.updated'),
              value: document.modifiedAt === null || document.modifiedAt === undefined ? null : <Timestamp value={document.modifiedAt} />,
            },
          ]}
        />
      </Section>

      {withStates && <LatestChanges documentId={document.id} periodKey={periodKey} sheets={sheetLabels(document)} />}
    </Stack>
  );
}

function Section({ title, hint, children }: { readonly title: string; readonly hint?: string | undefined; readonly children: ReactNode }): JSX.Element {
  return (
    <Stack gap="xs" component="section">
      <Group gap="xs" align="baseline">
        <Title order={3} size="sm">
          {title}
        </Title>
        {hint !== undefined && (
          <Text size="xs" c="dimmed">
            {hint}
          </Text>
        )}
      </Group>
      {children}
    </Stack>
  );
}

/** Заповненість аркуша — з `TableStatusDto` лише ЙОГО таблиць (`R-13`: є що вводити). */
export function sheetFill(tables: readonly TableStatus[], sheetCode: string): { filled: number; total: number } {
  const own = tables.filter((table) => table.sheetCode === sheetCode && table.inputCells > 0);

  return { filled: own.filter((table) => table.filledCells >= table.inputCells).length, total: own.length };
}

function SheetsSection({ document, periodKey }: { readonly document: DocumentSummary; readonly periodKey: number }): JSX.Element {
  const tables = useTableStatus(document.id, periodKey);
  const sheets = sheetLabels(document);
  const approved = sheets.filter((sheet) => sheet.state === 'Approved').length;

  return (
    <Section title={t('documents.sheets')} hint={t('segments.approvedOf', { done: approved, total: sheets.length })}>
      <Box role="list" data-quick-look-sheets="">
        {sheets.map((sheet) => {
          const fill = tables.data === undefined ? null : sheetFill(tables.data, sheet.code);

          return (
            <Group
              key={sheet.code}
              role="listitem"
              justify="space-between"
              wrap="nowrap"
              py="xs"
              style={{ borderBottom: '1px solid var(--ecr-border)' }}
              data-quick-look-sheet={sheet.code}
            >
              <Box>
                <Text size="sm" fw={500}>
                  {sheet.label}
                </Text>
                {fill !== null && fill.total > 0 && (
                  <Text size="xs" c="dimmed" data-quick-look-fill={`${String(fill.filled)}/${String(fill.total)}`}>
                    {t('documents.quickLookTablesFilled', { filled: fill.filled, total: fill.total })}
                  </Text>
                )}
              </Box>
              <StatusBadge kind="sheet" state={sheet.state} quiet />
            </Group>
          );
        })}
      </Box>
      {/* Заповненість не прочиталась — аркуші однаково видно, причина поруч. */}
      {tables.error !== null && <ErrorAlert error={tables.error} onRetry={() => void tables.refetch()} />}
    </Section>
  );
}

/** Останні події, новіші першими. */
export function latestEvents(events: readonly WorkflowEvent[], count = LatestChangesCount): WorkflowEvent[] {
  return [...events].sort((a, b) => b.at.localeCompare(a.at)).slice(0, count);
}

function LatestChanges({
  documentId,
  periodKey,
  sheets,
}: {
  readonly documentId: number;
  readonly periodKey: number;
  readonly sheets: readonly SheetLabel[];
}): JSX.Element | null {
  const history = useWorkflowHistory(documentId, periodKey, true);
  const labelOf = new Map(sheets.map((sheet) => [sheet.code, sheet.label]));

  /*
   * ⛔ Друга лінія захисту прихованого аркуша: подія аркуша, якого немає серед
   * видимих (`sheets` з тієї самої відповіді сервера), не показується, навіть
   * якщо журнал її віддав.
   */
  const events = history.data?.filter((event) => labelOf.has(event.sheetCode));

  // ⛔ `D15-06`: немає даних історії — блок не малюється (не «порожньо»).
  if (events !== undefined && events.length === 0) return null;

  return (
    <Section title={t('documents.quickLookLatestChanges')}>
      {history.error !== null ? (
        <ErrorAlert error={history.error} onRetry={() => void history.refetch()} />
      ) : events === undefined ? (
        <Skeleton height={48} radius="sm" />
      ) : (
        <Stack gap="xs" role="list" data-quick-look-changes="">
          {latestEvents(events).map((event, index) => (
            // ⚠ Свого ідентифікатора подія не має; перелік лише читається.
            <Group key={index} role="listitem" justify="space-between" wrap="nowrap" gap="sm" data-quick-look-event="">
              <Group gap="xs" wrap="nowrap">
                <Text size="sm" fw={500}>
                  {labelOf.get(event.sheetCode)}
                </Text>
                <StatusBadge kind="sheet" state={event.toState} quiet />
              </Group>
              <Text size="xs" c="dimmed">
                {event.byDisplayName} · <Timestamp value={event.at} />
              </Text>
            </Group>
          ))}
        </Stack>
      )}
    </Section>
  );
}
