import { badgeLook } from '@/shared/ui/StatusBadge';
import { lazy, Suspense, useEffect, useState, type JSX } from 'react';
import { Badge, Button, Group, Popover, Text, VisuallyHidden } from '@mantine/core';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import type { components } from '@/api/schema';
import { formatPeriodKey } from '@/shared/format';
import { t } from '@/shared/i18n';
import { untilTextInZone } from '@/shared/siteMoment';
import { getDocumentPeriodReopen, periodReopenKey } from './api';

type PeriodState = components['schemas']['PeriodState'];

/** Діалог — окремим чанком (`D-132`): поле дати й часу потрібне лише тому, хто відкриває місяць. */
const loadDialog = () => import('./DocumentPeriodReopenDialog');
const DocumentPeriodReopenDialog = lazy(async () => ({ default: (await loadDialog()).DocumentPeriodReopenDialog }));

/** Найдовша пауза `setTimeout` (2^31 − 1 мс): довший строк перевіряється ще раз після неї. */
const MaxTimerMs = 2_147_483_647;

/**
 * PERF-16: random 0..30 s added to the expiry timer. ⛔ Without it every viewer of a period whose reopen window ends
 * (the same wall-clock moment for all of them) refetches `slices.all()` in the same second. The server enforces the
 * lock regardless; the spread only delays the screen catching up.
 */
export const ReopenExpirySpreadMs = 30_000;

/** Delay of the expiry refresh for a window that ends in `left` ms; `random` is injectable for tests. */
export function reopenExpiryDelayMs(left: number, random: () => number = Math.random): number {
  const spread = Math.floor(Math.min(Math.max(random(), 0), 0.999_999) * ReopenExpirySpreadMs);

  return Math.min(Math.max(left, 0) + 1_000 + spread, MaxTimerMs);
}

interface DocumentPeriodReopenArgs {
  readonly documentId: number;
  readonly periodKey: number;
  /** Стан місяця з календаря проєкту (той самий для всіх документів). */
  readonly periodState: PeriodState | undefined;
  /**
   * Пояс майданчика (IANA) з календаря проєкту: строк у банері — у ньому, як у рядку місяця «Проєкти → Періоди»
   * (p3-1110: тут стояв пояс браузера, і той самий строк читався «Nov 10, 9:00 PM» проти «Nov 11, 12:00 AM»).
   * Поки календар не прийшов — пояс браузера.
   */
  readonly timeZoneId?: string | null | undefined;
}

/** Строк у поясі майданчика тим самим форматом, що й рядок місяця в PeriodsPage; без поясу — пояс браузера. */
const untilText = untilTextInZone;

interface DocumentPeriodReopen {
  /** Стан місяця ДЛЯ ЦЬОГО документа: діючий виняток робить закритий місяць `Grace` (як сервер). */
  readonly periodState: PeriodState | undefined;
  /** Дія в банері закритого періоду: кнопка або недоступна кнопка з причиною словами; `null` — дії немає. */
  readonly lockAction: JSX.Element | null;
  /** «Reopened for this document until …» — поки виняток діє. */
  readonly banner: JSX.Element | null;
  /** Діалог — поза банером: банер зникає, щойно місяць відкрито. */
  readonly dialog: JSX.Element | null;
}

/**
 * Приймальна 08.11 (i): закритий місяць, відкритий лише для ЦЬОГО документа (слово людини 08.11: «якщо мені потрібно
 * відкрити тільки для одного документу?»).
 *
 * ⚠ Запит — лише коли місяць закритий: у відкритому виняток нічого не змінює. Право й причину відмови вирішує
 * сервер (`Period.Reopen` у проєкті документа), клієнт лише показує їх. Решта документів місяця лишається закритою.
 *
 * ⛔ Строк діє сам: щойно `openUntil` минає, сторінка перечитує стан, документ і зрізи — сітка знову замкнена без
 * перезавантаження.
 */
export function useDocumentPeriodReopen({ documentId, periodKey, periodState, timeZoneId }: DocumentPeriodReopenArgs): DocumentPeriodReopen {
  const closed = periodState === 'Closed';
  const queryClient = useQueryClient();
  const [opened, setOpened] = useState(false);

  const query = useQuery({
    queryKey: periodReopenKey(documentId, periodKey),
    queryFn: () => getDocumentPeriodReopen(documentId, periodKey),
    enabled: closed,
  });

  const state = closed ? query.data : undefined;
  const active = state?.active ?? null;
  const openUntil = active?.openUntil ?? null;

  useEffect(() => {
    if (openUntil === null) return undefined;

    const left = Date.parse(openUntil) - Date.now();
    const timer = setTimeout(() => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.document.ofDocument(documentId) });
      void queryClient.invalidateQueries({ queryKey: queryKeys.slices.all() });
    }, reopenExpiryDelayMs(left));

    return () => clearTimeout(timer);
  }, [openUntil, documentId, queryClient]);

  const period = formatPeriodKey(periodKey);

  let lockAction: JSX.Element | null = null;
  if (closed && query.isPending) {
    // Keep the action's place while its state loads: a late button shifted the sheet panel below it.
    lockAction = (
      <Button size="xs" variant="light" loading disabled data-testid="document-period-reopen-pending" aria-hidden="true" tabIndex={-1}>
        {t('document.reopen.action', { period })}
      </Button>
    );
  } else if (state !== undefined && active === null) {
    if (state.canOpen) {
      lockAction = (
        <Button size="xs" variant="light" onClick={() => setOpened(true)} data-testid="document-period-reopen">
          {t('document.reopen.action', { period })}
        </Button>
      );
    } else if (state.denial === 'NoPermission') {
      // i6: дія видима, але недоступна — і причина словами поруч, а не лише в підказці.
      lockAction = (
        <Group gap="xs" wrap="wrap">
          <Button size="xs" variant="light" disabled data-testid="document-period-reopen">
            {t('document.reopen.action', { period })}
          </Button>
          <Text size="sm" data-testid="document-period-reopen-denied">
            {t('document.reopen.noPermission')}
          </Text>
        </Group>
      );
    }
  }

  // ✎ DOCUX L8 (№9): «відкрито для документа до …» — ЧИП у рядку стану (хто, чому — у ⓘ і для читалки), а не банер.
  const banner =
    active === null ? null : (
      <Popover position="bottom-start" withArrow shadow="md" width={360}>
        <Popover.Target>
          <Badge
            component="button"
            type="button"
            size="sm"
            variant="transparent"
            radius="xs"
            c={badgeLook('info').text}
            bg={badgeLook('info').bg}
            styles={{ root: { textTransform: 'none', letterSpacing: 'normal', fontWeight: 500, cursor: 'pointer' } }}
            data-testid="document-period-reopened"
          >
            {`${t('document.reopen.activeTitle', { until: untilText(active.openUntil, timeZoneId) })} ⓘ`}
            <VisuallyHidden>
              {` ${t('document.reopen.activeBody', { period, reason: active.reason, by: active.openedByName ?? String(active.openedByUserId) })}`}
            </VisuallyHidden>
          </Badge>
        </Popover.Target>
        <Popover.Dropdown>
          <Text size="sm">
            {t('document.reopen.activeBody', { period, reason: active.reason, by: active.openedByName ?? String(active.openedByUserId) })}
          </Text>
        </Popover.Dropdown>
      </Popover>
    );

  const dialog =
    opened && state !== undefined ? (
      <Suspense fallback={null}>
        <DocumentPeriodReopenDialog
          documentId={documentId}
          periodKey={periodKey}
          defaultOpenUntil={state.defaultOpenUntil}
          timeZoneId={timeZoneId}
          onClose={() => setOpened(false)}
        />
      </Suspense>
    ) : null;

  return {
    periodState: active !== null && periodState === 'Closed' ? 'Grace' : periodState,
    lockAction,
    banner,
    dialog,
  };
}
