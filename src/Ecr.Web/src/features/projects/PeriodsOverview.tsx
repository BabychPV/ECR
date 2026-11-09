import { type JSX } from 'react';
import { Box, Group, SimpleGrid, Stack, Text, Title, UnstyledButton } from '@mantine/core';
import type { PeriodCalendarDto } from '@/api/types';
import { formatCount, formatPeriodKey } from '@/shared/format';
import { wholeDaysUntil } from '@/shared/siteMoment';
import { effectivePeriodState, lockedSinceOf } from './periodEffective';
import { StatusBadge, statusKey } from '@/shared/ui/StatusBadge';
import { t } from '@/shared/i18n';

/**
 * UI-33 — плитки місяців року на вкладці Periods проєкту (`docs/design/hybrid/screens-ops.js`).
 *
 * ✎ Projects (макет 08.11): смугу показників і таблицю «All projects · current period» замінив перелік
 * `/projects` (`ProjectsPage`) — тут лишились плитки року й правила підписів місяця.
 *
 * ⚠ Окремий модуль за `lazy()` зі сторінки: чанк `PeriodsPage` близько межі `D-132`.
 *
 * ⛔ Що з макета НЕ взято (картка UI-33, рішення 1): головна дія «Open
 * October» / «Close now» — період відкриває й закриває `PeriodStateJob`, а
 * людина лише перевідкриває закритий із причиною (кнопка «Reopen» у
 * календарі сторінки). Колонки «Not submitted» і «Late edits» — даних по
 * проєкту в API немає (`PeriodCalendarDto`, `campaign/summary` їх не несуть),
 * а порожня колонка «0» була б неправдою (`D15-06`).
 */

/** Підпис дати в поясі майданчика; дає сторінка (`siteMomentText`), щоб правило поясу жило в одному місці. */
export type SiteDate = (value: string, zone: string, inclusiveEnd: boolean) => string | null;

type Period = PeriodCalendarDto['periods'][number];

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

  // ✎ UAT 08.11 (e2): межа блокування вже минула — днів «до закриття» немає (не «closes today»).
  if (effectivePeriodState(period, now) === 'Closed') return null;

  return wholeDaysUntil(period.endsAt, now);
}

function closesText(days: number): string {
  return days === 0 ? t('periods.tile.closesToday') : formatCount(days, 'periods.tile.closesIn');
}

/**
 * Рядок-пояснення плитки: що з періодом зараз і до коли (макет `perNote`).
 *
 * ✎ Pass2 п. 6 (людський прохід 07.11, P3): у проєкті не в стані `Active` (чернетка, архів) періоди не відкриває
 * ніхто — `PeriodStateJob` веде лише активні, — тож «opens Jan 1, 2026» на плитці обіцяв дату, яка вже минула і не
 * настане. `projectStatus` не задано — поведінка як раніше (активний проєкт).
 */
export function periodNote(
  period: Period,
  calendar: PeriodCalendarDto,
  siteDate: SiteDate,
  now: number,
  projectStatus?: string,
): string {
  const zone = calendar.timeZoneId;

  if (period.state === 'Scheduled' && projectStatus !== undefined && projectStatus !== 'Active') {
    return projectStatus === 'Draft' ? t('periods.tile.notOpenDraft') : t('periods.tile.notOpenInactive');
  }
  const end = siteDate(period.endsAt, zone, true) ?? '';

  // ✎ UAT 08.11 (e2): «Locked since 17 Mar 2026», а не «closes today» для минулої межі — ефективний стан.
  if (effectivePeriodState(period, now) === 'Closed') {
    return t('periods.tile.lockedSince', { date: siteDate(lockedSinceOf(period), zone, false) ?? '' });
  }

  if (period.reopenedUntil !== null && (period.state === 'Open' || period.state === 'Grace')) {
    return t('periods.tile.reopened', { date: siteDate(period.reopenedUntil, zone, false) ?? '' });
  }

  if (period.state === 'Grace') return t('periods.tile.grace', { date: end });

  if (period.state === 'Open') {
    const days = daysLeft(period, now) ?? 0;

    return `${closesText(days)} · ${end}`;
  }

  if (period.state === 'Scheduled') {
    // ⚠ `startsAt` — це вже `ComputedOpenAt` (`PeriodStart + OpenOffsetDays`, `GetPeriodCalendarHandler`):
    // зсув політики вдруге додавати не можна (N4-01).
    return t('periods.tile.opens', { date: siteDate(period.startsAt, zone, false) ?? '' });
  }

  return t('periods.tile.readOnly');
}

interface PeriodYearTilesProps {
  readonly calendar: PeriodCalendarDto;
  readonly projectCode: string;
  /** Стан проєкту (`Draft`/`Active`/`Archived`); не `Active` — плитки не обіцяють дат відкриття (Pass2 п. 6). */
  readonly projectStatus?: string | undefined;
  readonly siteDate: SiteDate;
  /**
   * ✎ debt-1109 i4-P3: «Locked · N document(s) reopened until …» закритого місяця з винятками (той самий рядок, що в
   * календарі нижче); `null`/немає — винятків немає. Плитки раніше показували просто «Locked since …».
   */
  readonly reopenedNote?: ((periodKey: number) => string | null) | undefined;
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
  projectStatus,
  siteDate,
  reopenedNote,
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
          // ✎ UAT 08.11 (e3): плитка показує той самий ЕФЕКТИВНИЙ стан, що й сітка.
          const shown = effectivePeriodState(period, now);
          const state = t(statusKey('period', shown));
          const reopened = shown === 'Closed' ? (reopenedNote?.(period.periodKey) ?? null) : null;
          const note = reopened ?? periodNote(period, calendar, siteDate, now, projectStatus);
          const pressed = selectedKey === period.periodKey;

          return (
            <UnstyledButton
              key={period.periodKey}
              data-period-tile={period.periodKey}
              data-state={shown}
              data-tile-reopened={reopened === null ? undefined : ''}
              aria-pressed={pressed}
              aria-label={`${caption}, ${state}. ${note}`}
              onClick={() => onPick(period.periodKey)}
              p="xs"
              // ⚠ «Ще не відкрито» — пунктирна межа, як у макеті (`.ops-month.notopened`), а не
              // прозорість: `opacity: 0.75` топила приглушений текст до 3.34:1 (batch-2-a, дефект 5).
              style={{
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
                {/* Рядок винятків сам каже «Locked» — без бейджа, як у календарі. */}
                {reopened === null && <StatusBadge kind="period" state={shown} quiet={shown !== 'Grace'} />}
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
