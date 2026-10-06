import { type JSX } from 'react';
import { Anchor, Box, Group, SimpleGrid, Stack, Text, Title, UnstyledButton } from '@mantine/core';
import { useQueries } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { PagedProjects, PeriodCalendarDto } from '@/api/types';
import { formatCount, formatPeriodKey } from '@/shared/format';
import { DataTable, type DataTableColumn } from '@/shared/ui/DataTable';
import { StatStrip } from '@/shared/ui/StatStrip';
import { StatusBadge, statusKey } from '@/shared/ui/StatusBadge';
import { TwoLine } from '@/shared/ui/TwoLine';
import { useUrlState } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';

/**
 * UI-33 — огляд періодів за макетом (`docs/design/hybrid/screens-ops.js`,
 * `/admin/periods`: смуга показників, плитки місяців року, таблиця «All
 * projects · current period»).
 *
 * ⚠ Окремий модуль за `lazy()` зі сторінки: чанк `PeriodsPage` близько межі
 * `D-132`, а таблиця набору (`DataTable`) і смуга (`StatStrip`) їй статично не
 * потрібні.
 *
 * ⛔ Що з макета НЕ взято (картка UI-33, рішення 1): головна дія «Open
 * October» / «Close now» — період відкриває й закриває `PeriodStateJob`, а
 * людина лише перевідкриває закритий із причиною (кнопка «Reopen» у
 * календарі сторінки). Колонки «Not submitted» і «Late edits» — даних по
 * проєкту в API немає (`PeriodCalendarDto`, `campaign/summary` їх не несуть),
 * а порожня колонка «0» була б неправдою (`D15-06`).
 */

/** Межа дня в мілісекундах — для «closes in N days». */
const DayMs = 24 * 60 * 60 * 1000;

/** Поріг показника «closing in ≤ 3 days» — як у макеті. */
const SoonDays = 3;

/** Підпис дати в поясі майданчика; дає сторінка (`siteMomentText`), щоб правило поясу жило в одному місці. */
export type SiteDate = (value: string, zone: string, inclusiveEnd: boolean) => string | null;

type Period = PeriodCalendarDto['periods'][number];
type ProjectSummary = PagedProjects['items'][number];

/**
 * Поточний період проєкту: позначений `isCurrent` (`D-77`), інакше перший
 * відкритий, інакше перший у пільговому строку. `null` — жодного.
 */
export function currentPeriodOf(calendar: PeriodCalendarDto): Period | null {
  return (
    calendar.periods.find((period) => period.isCurrent) ??
    calendar.periods.find((period) => period.state === 'Open') ??
    calendar.periods.find((period) => period.state === 'Grace') ??
    null
  );
}

/**
 * Повних діб до жорсткого закриття (`endsAt` — виключна межа). `0` — останній
 * день сьогодні. `null` — період не приймає даних (не `Open`/`Grace`).
 */
export function daysLeft(period: Period, now: number): number | null {
  if (period.state !== 'Open' && period.state !== 'Grace') return null;

  const left = Date.parse(period.endsAt) - now;
  if (!Number.isFinite(left)) return null;

  return Math.max(0, Math.floor(left / DayMs));
}

function closesText(days: number): string {
  return days === 0 ? t('periods.tile.closesToday') : formatCount(days, 'periods.tile.closesIn');
}

/** Рядок-пояснення плитки: що з періодом зараз і до коли (макет `perNote`). */
export function periodNote(
  period: Period,
  calendar: PeriodCalendarDto,
  siteDate: SiteDate,
  now: number,
): string {
  const zone = calendar.timeZoneId;
  const end = siteDate(period.endsAt, zone, true) ?? '';

  if (period.reopenedUntil !== null && (period.state === 'Open' || period.state === 'Grace')) {
    return t('periods.tile.reopened', { date: siteDate(period.reopenedUntil, zone, false) ?? '' });
  }

  if (period.state === 'Grace') return t('periods.tile.grace', { date: end });

  if (period.state === 'Open') {
    const days = daysLeft(period, now) ?? 0;

    return `${closesText(days)} · ${end}`;
  }

  if (period.state === 'Scheduled') {
    const opens = Date.parse(period.startsAt) + calendar.policy.openOffsetDays * DayMs;

    return t('periods.tile.opens', {
      date: siteDate(new Date(opens).toISOString(), zone, false) ?? '',
    });
  }

  return t('periods.tile.readOnly');
}

interface PeriodYearTilesProps {
  readonly calendar: PeriodCalendarDto;
  readonly projectCode: string;
  readonly siteDate: SiteDate;
  /** Ключ виділеної плитки (`aria-pressed`). */
  readonly selectedKey: number | null;
  readonly onPick: (periodKey: number) => void;
}

/**
 * Плитки періодів року поточного періоду проєкту (макет: `.ops-year`,
 * `.ops-month`). Клац — виділяє рядок цього періоду в календарі нижче, де
 * стоять дії над ним.
 */
export function PeriodYearTiles({
  calendar,
  projectCode,
  siteDate,
  selectedKey,
  onPick,
}: PeriodYearTilesProps): JSX.Element | null {
  const now = Date.now();
  const year = currentPeriodOf(calendar)?.year ?? calendar.periods.at(-1)?.year;
  if (year === undefined) return null;

  const periods = calendar.periods.filter((period) => period.year === year);

  return (
    <Box component="section" mb="md" aria-labelledby="periods-year-title" data-testid="periods-year">
      <Title order={2} size="h5" id="periods-year-title" mb="xs">
        {t('periods.year.title', { project: projectCode, year })}
      </Title>

      <SimpleGrid cols={{ base: 2, xs: 3, md: 4, lg: 6 }} spacing="xs" role="group" aria-labelledby="periods-year-title">
        {periods.map((period) => {
          const caption = formatPeriodKey(period.periodKey, calendar.periodKind);
          const state = t(statusKey('period', period.state));
          const note = periodNote(period, calendar, siteDate, now);
          const pressed = selectedKey === period.periodKey;

          return (
            <UnstyledButton
              key={period.periodKey}
              data-period-tile={period.periodKey}
              data-state={period.state}
              aria-pressed={pressed}
              aria-label={`${caption}, ${state}. ${note}`}
              onClick={() => onPick(period.periodKey)}
              p="xs"
              style={{
                // a11y: майбутній період — пунктирна межа, а не `opacity: 0.75`:
                // прозорість опускала `dimmed` до 3.34 < 4.5 (axe, світла тема).
                border: `1px ${period.state === 'Scheduled' && !pressed ? 'dashed' : 'solid'} ${pressed ? 'var(--mantine-primary-color-filled)' : 'var(--mantine-color-default-border)'}`,
                borderRadius: 'var(--mantine-radius-sm)',
              }}
            >
              <Stack gap="xs" align="flex-start">
                <Group gap="xs" wrap="nowrap">
                  <Text size="sm" fw={600}>
                    {caption}
                  </Text>
                  {period.isCurrent && (
                    <Text span size="xs" c="dimmed">
                      · {t('periods.tile.now')}
                    </Text>
                  )}
                </Group>
                <StatusBadge kind="period" state={period.state} quiet={period.state !== 'Grace'} />
                <Text size="xs" c="dimmed">
                  {note}
                </Text>
              </Stack>
            </UnstyledButton>
          );
        })}
      </SimpleGrid>
    </Box>
  );
}

interface OverviewRow {
  readonly project: ProjectSummary;
  readonly calendar: PeriodCalendarDto | undefined;
  readonly current: Period | null;
  readonly grace: Period | null;
  readonly left: number | null;
}

type StatId = 'open' | 'grace' | 'soon';

const statMatch: Record<StatId, (row: OverviewRow) => boolean> = {
  open: (row) => row.current?.state === 'Open',
  grace: (row) => row.grace !== null,
  soon: (row) => row.left !== null && row.left <= SoonDays,
};

function isStat(value: string | null): value is StatId {
  return value === 'open' || value === 'grace' || value === 'soon';
}

interface AllProjectsPeriodsProps {
  readonly projects: readonly ProjectSummary[];
  readonly selectedId: number | null;
  readonly onPick: (projectId: number) => void;
  readonly siteDate: SiteDate;
}

/**
 * «All projects · current period» + смуга показників над нею (макет).
 *
 * ⚠ Календарі — тим самим ключем `['periods', id]`, що й календар сторінки та
 * автовибір періоду в `DocumentsPage` (A3-02): кеш спільний, обраний проєкт
 * удруге не запитується.
 */
export function AllProjectsPeriods({ projects, selectedId, onPick, siteDate }: AllProjectsPeriodsProps): JSX.Element {
  const [statRaw, setStat] = useUrlState('stat');
  const stat = isStat(statRaw) ? statRaw : null;
  const now = Date.now();

  const calendars = useQueries({
    queries: projects.map((project) => ({
      queryKey: ['periods', project.id],
      queryFn: () => apiFetch<PeriodCalendarDto>(`/api/v1/projects/${String(project.id)}/periods`),
    })),
  });

  const rows: OverviewRow[] = projects.map((project, index) => {
    const calendar = calendars[index]?.data;
    const current = calendar === undefined ? null : currentPeriodOf(calendar);

    return {
      project,
      calendar,
      current,
      grace: calendar?.periods.find((period) => period.state === 'Grace') ?? null,
      left: current === null ? null : daysLeft(current, now),
    };
  });

  const failure = calendars.find((entry) => entry.error !== null)?.error ?? null;
  const pending = calendars.some((entry) => entry.isPending);
  const visible = stat === null ? rows : rows.filter(statMatch[stat]);

  const columns: readonly DataTableColumn<OverviewRow>[] = [
    {
      key: 'project',
      label: t('periods.overview.project'),
      sortValue: (row) => row.project.code,
      render: (row) => (
        <TwoLine
          primary={
            <Anchor
              component="button"
              type="button"
              size="sm"
              onClick={(event) => {
                // Клац рядка теж обирає — не обирати двічі.
                event.stopPropagation();
                onPick(row.project.id);
              }}
            >
              {row.project.code}
            </Anchor>
          }
          secondary={t(statusKey('project', row.project.status))}
        />
      ),
    },
    {
      key: 'current',
      label: t('periods.overview.current'),
      sortValue: (row) => row.current?.periodKey ?? null,
      render: (row) =>
        row.current === null ? null : formatPeriodKey(row.current.periodKey, row.project.periodKind),
    },
    {
      key: 'state',
      label: t('periods.overview.state'),
      sortable: false,
      render: (row) =>
        row.current === null ? null : (
          <Group gap="xs" wrap="nowrap">
            <StatusBadge kind="period" state={row.current.state} quiet />
            {row.grace !== null && row.grace.periodKey !== row.current.periodKey && (
              <>
                <StatusBadge kind="period" state="Grace" />
                <Text span size="xs" c="dimmed">
                  {t('periods.overview.inGrace', {
                    period: formatPeriodKey(row.grace.periodKey, row.project.periodKind),
                  })}
                </Text>
              </>
            )}
          </Group>
        ),
    },
    {
      key: 'closes',
      label: t('periods.overview.closes'),
      sortValue: (row) => row.left,
      render: (row) => {
        if (row.current === null || row.left === null || row.calendar === undefined) return null;
        const date = siteDate(row.current.endsAt, row.calendar.timeZoneId, true) ?? '';

        return (
          <Text span size="sm" {...(row.left <= SoonDays ? { fw: 600, c: 'statusWarning' } : {})}>
            {`${date} · ${closesText(row.left)}`}
          </Text>
        );
      },
    },
  ];

  return (
    <Box component="section" mt="lg" aria-labelledby="periods-overview-title" data-testid="periods-overview">
      <Group gap="xs" align="baseline" mb="xs">
        <Title order={2} size="h5" id="periods-overview-title">
          {t('periods.overview.title')}
        </Title>
        <Text size="xs" c="dimmed">
          {t('periods.overview.hint')}
        </Text>
      </Group>

      <StatStrip
        label={t('periods.overview.stats')}
        items={[
          { id: 'open', label: t('periods.overview.stat.open'), value: rows.filter(statMatch.open).length },
          {
            id: 'grace',
            label: t('periods.overview.stat.grace'),
            value: rows.filter(statMatch.grace).length,
            tone: 'warning',
          },
          {
            id: 'soon',
            label: t('periods.overview.stat.soon'),
            value: rows.filter(statMatch.soon).length,
            tone: 'warning',
          },
        ]}
        active={stat}
        onSelect={(id) => setStat(id)}
      />

      <DataTable<OverviewRow>
        columns={columns}
        rows={pending && failure === null ? undefined : visible}
        rowKey={(row) => String(row.project.id)}
        isPending={pending && failure === null}
        error={failure}
        onRetry={() => void Promise.all(calendars.map((entry) => entry.refetch()))}
        filtered={stat !== null}
        emptyTitle={t('periods.noProjects')}
        noMatchTitle={t('periods.overview.noMatch')}
        onClearFilters={() => setStat(null)}
        onRowClick={(row) => onPick(row.project.id)}
        {...(selectedId === null ? {} : { selectedKey: String(selectedId) })}
        defaultSort={{ key: 'project', direction: 'asc' }}
      />
    </Box>
  );
}
