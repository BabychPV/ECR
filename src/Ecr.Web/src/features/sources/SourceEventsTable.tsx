import { lazy, Suspense, useMemo, useState, type JSX } from "react";
import {
  Anchor,
  Badge,
  Button,
  Group,
  Loader,
  Select,
  Stack,
  Table,
  Text,
  Title,
} from "@mantine/core";
import { notifications } from "@mantine/notifications";
import { useInfiniteQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { Link } from "react-router-dom";
import { EcrApiError } from "@/api/client";
import type { DocumentSummary } from "@/api/types";
import { formatDateTime, formatNumber } from "@/shared/format";
import { useFocusAfterBusy } from "@/shared/a11y/focus";
import { t } from "@/shared/i18n";
import { ErrorAlert } from "@/shared/ui/ErrorAlert";
import { PeriodPicker } from "@/shared/ui/PeriodPicker";
import { humanizeJobId } from "@/features/workflow/jobLabel";
import { documentLabel } from "./SourceEventMapsPanel";
import {
  SourceEventStatuses,
  statusColor,
  statusExplanation,
  statusLabel,
} from "./sourceEventStatus";
import {
  fetchSourceEvents,
  syncSourceEvents,
  type SourceEventLinkStatus,
  type SourceEventMap,
  type SourceEventRow,
  type SourceEventsQuery,
} from "./sourceEventsApi";
import { documentHref, SourceEventsKeys } from "./sourceEventsData";

/** Розмір сторінки таблиці подій: сервер приймає 1..500. */
export const EventsPageSize = 50;

/**
 * `@mantine/dates` — за `import()`, як у `CollectionRunsPanel.tsx`: діапазон дат відкриває не кожен, а чанк
 * сторінки джерел рахується в бюджет (`D-132`).
 */
const DateInput = lazy(async () => {
  const module = await import("@/shared/dates/DateInputWithStyles");

  return { default: module.DateInput };
});

/** `Date` з поля → `YYYY-MM-DD` за МІСЦЕВИМ календарем (те, що людина обрала). */
function dateOnly(date: Date): string {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
}

function parseDateOnly(value: string | null): Date | null {
  if (value === null) return null;
  const parsed = new Date(`${value}T00:00:00`);

  return Number.isNaN(parsed.getTime()) ? null : parsed;
}

/**
 * Межа доби в UTC: `from` — початок обраної доби, `to` — початок НАСТУПНОЇ (контракт `toUtc` — «раніше»).
 *
 * ⚠ Доби — UTC, а не пояс проєкту: пояс приходить лише з рядками, а фільтр треба зібрати до першого рядка.
 * Підпис поля каже це прямо («UTC»).
 */
export function dayBoundUtc(value: string, next: boolean): string {
  const [year, month, day] = value.split("-").map(Number);

  return new Date(
    Date.UTC(year ?? 1970, (month ?? 1) - 1, (day ?? 1) + (next ? 1 : 0)),
  ).toISOString();
}

/**
 * Момент у поясі проєкту (`timeZoneId` рядка), мовою інтерфейсу.
 *
 * ⚠ Сервер віддає і `startLocal` (зі зсувом), і `startUtc`; форматується UTC з `timeZone` проєкту — тоді
 * браузер у будь-якому поясі показує рівно час ділянки. Невідомий браузеру пояс — сирий локальний рядок сервера,
 * а не мовчки час браузера.
 */
export function formatInZone(
  utc: string | null,
  timeZoneId: string,
  fallback: string | null,
): string {
  if (utc === null) return "";

  try {
    return formatDateTime(utc, {
      dateStyle: "medium",
      timeStyle: "medium",
      timeZone: timeZoneId,
    });
  } catch {
    return fallback ?? utc;
  }
}

/** Той самий момент в UTC — для підказки. */
function formatUtc(utc: string | null): string {
  return utc === null
    ? ""
    : `${formatDateTime(utc, { dateStyle: "medium", timeStyle: "medium", timeZone: "UTC" })} UTC`;
}

/** `422 eventSyncNoMap`: у сутності немає активного мапінгу — підказка «створіть мапінг», а не лише відмова. */
function isNoMap(error: unknown): boolean {
  return (
    error instanceof EcrApiError &&
    error.problem.status === 422 &&
    error.problem.extensions2?.["messageKey"] ===
      "err.ECR-INT-0422.eventSyncNoMap"
  );
}

interface Filters {
  readonly mapId: number | null;
  readonly statuses: readonly SourceEventLinkStatus[];
  readonly periodKey: number | null;
  readonly from: string | null;
  readonly to: string | null;
}

const NoFilters: Filters = {
  mapId: null,
  statuses: [],
  periodKey: null,
  from: null,
  to: null,
};

/** Фільтри форми → рядок запиту сервера (без курсора). */
export function eventsQuery(filters: Filters): SourceEventsQuery {
  return {
    ...(filters.mapId === null ? {} : { mapId: filters.mapId }),
    ...(filters.statuses.length === 0 ? {} : { status: filters.statuses }),
    ...(filters.periodKey === null ? {} : { periodKey: filters.periodKey }),
    ...(filters.from === null
      ? {}
      : { fromUtc: dayBoundUtc(filters.from, false) }),
    ...(filters.to === null ? {} : { toUtc: dayBoundUtc(filters.to, true) }),
    limit: EventsPageSize,
  };
}

function EventRow({ row }: { readonly row: SourceEventRow }): JSX.Element {
  return (
    <Table.Tr data-source-event-row={row.id}>
      <Table.Td>
        {/* ⚠ UTC — у `title`, а не в `Tooltip`: Mantine `Tooltip` тут виносив спільний чанк у вхідний і зсував
            бюджет `DocumentPage` (248.6 → 253.5 КБ, `D-132`). */}
        <Text size="sm" data-event-start="">
          {formatInZone(row.startUtc, row.timeZoneId, row.startLocal)}
        </Text>
        {/* UTC — видимим текстом (не лише `title`): доступний з клавіатури й сенсорних. */}
        <Text size="xs" c="dimmed" data-event-start-utc="">
          {formatUtc(row.startUtc)}
        </Text>
        {row.endUtc === null ? (
          <Text size="xs" c="dimmed">
            {t("sourceEvents.stillOpen")}
          </Text>
        ) : (
          <>
            <Text size="xs" c="dimmed" data-event-end="">
              {formatInZone(row.endUtc, row.timeZoneId, row.endLocal)}
            </Text>
            <Text size="xs" c="dimmed" data-event-end-utc="">
              {formatUtc(row.endUtc)}
            </Text>
          </>
        )}
      </Table.Td>
      <Table.Td>
        <Text size="sm">{row.eventName ?? row.sourceEventId}</Text>
        {row.primaryElement !== null && (
          <Text size="xs" c="dimmed" ff="monospace" data-event-element="">
            {row.primaryElement}
          </Text>
        )}
      </Table.Td>
      <Table.Td>
        <Badge
          variant="light"
          color={statusColor(row.status)}
          title={statusExplanation(row.status)}
          data-event-status={row.status}
        >
          {statusLabel(row.status)}
        </Badge>
      </Table.Td>
      <Table.Td>
        {row.rowKey !== null && (
          <Text size="xs" ff="monospace" data-event-row-key="">
            {row.rowKey}
          </Text>
        )}
      </Table.Td>
      <Table.Td>
        <Anchor
          component={Link}
          to={documentHref(row.documentId, row.periodKey)}
          size="sm"
          data-event-document=""
        >
          {row.documentKey}
        </Anchor>
        {row.periodKey !== null && (
          <Text size="xs" c="dimmed">
            {row.periodKey}
          </Text>
        )}
      </Table.Td>
      <Table.Td>
        <Stack gap="xs">
          {row.keptManual.length > 0 && (
            <Group gap="xs" data-event-kept-manual="">
              <Text
                size="xs"
                c="dimmed"
                title={t("sourceEvents.keptManualHint")}
              >
                {t("sourceEvents.keptManual")}
              </Text>
              {row.keptManual.map((column) => (
                <Badge key={column} variant="outline" color="gray" size="sm">
                  {column}
                </Badge>
              ))}
            </Group>
          )}
          {row.unmapped.length > 0 && (
            <Stack gap="xs" data-event-unmapped="">
              <Text size="xs" c="dimmed">
                {t("sourceEvents.unmapped")}
              </Text>
              {row.unmapped.map((item) => (
                <Text
                  key={`${item.column}:${item.value ?? ""}`}
                  size="xs"
                  ff="monospace"
                >
                  {item.column}: {item.value ?? t("sourceEvents.emptyValue")}
                </Text>
              ))}
            </Stack>
          )}
        </Stack>
      </Table.Td>
    </Table.Tr>
  );
}

/**
 * Таблиця подій сутності (`GET /sources/{id}/source-events`): час у поясі проєкту з UTC у підказці, назва й
 * первинний елемент, стан із поясненням, ключ рядка `EF-…`, документ і період посиланням, колонки, лишені за
 * людиною, і значення без відповідника. Фільтри — мапінг, стани, період, діапазон дат; далі — «Показати ще»
 * курсором, без повного перечитування.
 *
 * ⛔ «Отримати з PI зараз» — лише з `Integration.Manage`; без активного мапінгу сервер відмовляє
 * (`422 eventSyncNoMap`), і тут показується не лише відмова, а й дія «Створити мапінг».
 */
export function SourceEventsTable({
  sourceEntityId,
  maps,
  documents,
  canManage,
  onCreateMap,
}: {
  readonly sourceEntityId: number;
  readonly maps: readonly SourceEventMap[];
  readonly documents: readonly DocumentSummary[];
  readonly canManage: boolean;
  readonly onCreateMap: () => void;
}): JSX.Element {
  const queryClient = useQueryClient();
  const [filters, setFilters] = useState<Filters>(NoFilters);
  const query = useMemo(() => eventsQuery(filters), [filters]);

  const events = useInfiniteQuery({
    queryKey: SourceEventsKeys.events(sourceEntityId, query),
    queryFn: ({ pageParam }) =>
      fetchSourceEvents(
        sourceEntityId,
        pageParam === null ? query : { ...query, cursor: pageParam },
      ),
    initialPageParam: null as string | null,
    getNextPageParam: (page) => page.nextCursor,
  });

  const sync = useMutation({
    // ⚠ Відмову показує `ErrorAlert` у рендері — без `handled` сітка додала б тост (L9-01).
    meta: { handled: true },
    mutationFn: () => syncSourceEvents(sourceEntityId),
    onSuccess: (accepted) => {
      void queryClient.invalidateQueries({
        queryKey: SourceEventsKeys.eventsOf(sourceEntityId),
      });
      notifications.show({
        message: t("sourceEvents.syncQueued", {
          job: humanizeJobId(accepted.jobId),
        }),
      });
    },
  });

  // ⚠ «Отримати з PI зараз» на час запиту `loading` (= `disabled`) — фокус повертається на кнопку.
  const syncFocus = useFocusAfterBusy(sync.isPending);

  const rows = events.data?.pages.flatMap((page) => page.items) ?? [];
  const total = events.data?.pages[0]?.totalCount ?? null;

  const mapOptions = maps.map((map) => ({
    value: String(map.id),
    label: t("sourceEvents.mapOption", {
      id: map.id,
      document: documentLabel(documents, map.documentId),
    }),
  }));

  return (
    <Stack gap="xs" data-source-events="">
      <Group justify="space-between">
        <Title order={4}>{t("sourceEvents.title")}</Title>
        {canManage && (
          <Button
            ref={syncFocus.ref}
            size="xs"
            loading={sync.isPending}
            onClick={() => {
              syncFocus.arm();
              sync.mutate();
            }}
            data-source-events-sync=""
          >
            {t("sourceEvents.syncNow")}
          </Button>
        )}
      </Group>

      {sync.error !== null && (
        <Stack gap="xs" data-source-events-sync-error="">
          <ErrorAlert error={sync.error} />
          {isNoMap(sync.error) && (
            <Group gap="xs">
              <Text size="sm">{t("sourceEvents.syncNoMapHint")}</Text>
              <Button
                size="xs"
                variant="default"
                onClick={onCreateMap}
                data-source-events-sync-create-map=""
              >
                {t("sourceEvents.mapCreate")}
              </Button>
            </Group>
          )}
        </Stack>
      )}

      <Group gap="sm" align="end" wrap="wrap" data-source-events-filters="">
        <Select
          size="xs"
          miw={200}
          label={t("sourceEvents.filterMap")}
          placeholder={t("sourceEvents.filterAll")}
          data={mapOptions}
          value={filters.mapId === null ? null : String(filters.mapId)}
          clearable
          onChange={(value) =>
            setFilters({
              ...filters,
              mapId: value === null ? null : Number(value),
            })
          }
          data-source-events-filter-map=""
        />
        <Stack gap="xs" data-source-events-filter-status="">
          <Text size="xs" fw={500}>
            {t("sourceEvents.filterStatus")}
          </Text>
          {/* ⚠ Кнопки-перемикачі, а не `MultiSelect`: той виносить спільний чанк комбобоксів у вхідний чанк і
              зсуває бюджет `DocumentPage` (248.6 → 253.5 КБ, `D-132`). */}
          <Group
            gap="xs"
            role="group"
            aria-label={t("sourceEvents.filterStatus")}
          >
            {SourceEventStatuses.map((status) => {
              const on = filters.statuses.includes(status);

              return (
                <Button
                  key={status}
                  size="compact-xs"
                  variant={on ? "filled" : "default"}
                  aria-pressed={on}
                  onClick={() =>
                    setFilters({
                      ...filters,
                      statuses: on
                        ? filters.statuses.filter((item) => item !== status)
                        : [...filters.statuses, status],
                    })
                  }
                  data-source-events-filter-status-option={status}
                >
                  {statusLabel(status)}
                </Button>
              );
            })}
          </Group>
        </Stack>
        <PeriodPicker
          label={t("sourceEvents.filterPeriod")}
          value={filters.periodKey}
          onChange={(value) => setFilters({ ...filters, periodKey: value })}
        />
        <Suspense fallback={null}>
          <DateInput
            size="xs"
            miw={150}
            label={t("sourceEvents.filterFromUtc")}
            valueFormat="YYYY-MM-DD"
            clearable
            value={parseDateOnly(filters.from)}
            onChange={(next) =>
              setFilters({
                ...filters,
                from: next === null ? null : dateOnly(next),
              })
            }
          />
          <DateInput
            size="xs"
            miw={150}
            label={t("sourceEvents.filterToUtc")}
            valueFormat="YYYY-MM-DD"
            clearable
            value={parseDateOnly(filters.to)}
            onChange={(next) =>
              setFilters({
                ...filters,
                to: next === null ? null : dateOnly(next),
              })
            }
          />
        </Suspense>
      </Group>

      {events.isError && (
        <ErrorAlert
          error={events.error}
          onRetry={() => void events.refetch()}
        />
      )}

      {/* ⛔ ФВ-14.25: перша сторінка в дорозі — завантаження, а не порожнє місце під фільтрами («подій немає»). */}
      {events.isPending && <Loader size="sm" />}

      {total !== null && (
        // ⚠ `role="status"`: після зміни фільтра читач чує нову кількість, а не мовчання.
        <Text size="xs" c="dimmed" role="status" data-source-events-total={total}>
          {t("sourceEvents.total", { count: formatNumber(total) })}
        </Text>
      )}

      {events.isSuccess && rows.length === 0 && (
        <Text size="sm" c="dimmed" data-source-events-empty="">
          {t("sourceEvents.empty")}
        </Text>
      )}

      {rows.length > 0 && (
        <Table.ScrollContainer minWidth={900}>
          <Table data-source-events-table="" aria-label={t('sourceEvents.title')}>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t("sourceEvents.colTime")}</Table.Th>
                <Table.Th>{t("sourceEvents.colName")}</Table.Th>
                <Table.Th>{t("sourceEvents.colStatus")}</Table.Th>
                <Table.Th>{t("sourceEvents.colRowKey")}</Table.Th>
                <Table.Th>{t("sourceEvents.colDocument")}</Table.Th>
                <Table.Th>{t("sourceEvents.colDetails")}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {rows.map((row) => (
                <EventRow key={row.id} row={row} />
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}

      {events.hasNextPage && (
        <Group justify="center">
          <Button
            size="xs"
            variant="default"
            loading={events.isFetchingNextPage}
            onClick={() => void events.fetchNextPage()}
            data-source-events-more=""
          >
            {t("sourceEvents.showMore")}
          </Button>
        </Group>
      )}
    </Stack>
  );
}
