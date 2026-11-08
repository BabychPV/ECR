import { lazy, Suspense, useEffect, useState, type JSX } from 'react';
import { Alert, Button, Group, Text } from '@mantine/core';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import type { components } from '@/api/schema';
import { formatPeriodKey } from '@/shared/format';
import { formatDateTime } from '@/shared/format/datetime';
import { t } from '@/shared/i18n';
import { getDocumentPeriodReopen, periodReopenKey } from './api';

type PeriodState = components['schemas']['PeriodState'];

/** Діалог — окремим чанком (`D-132`): поле дати й часу потрібне лише тому, хто відкриває місяць. */
const loadDialog = () => import('./DocumentPeriodReopenDialog');
const DocumentPeriodReopenDialog = lazy(async () => ({ default: (await loadDialog()).DocumentPeriodReopenDialog }));

/** Найдовша пауза `setTimeout` (2^31 − 1 мс): довший строк перевіряється ще раз після неї. */
const MaxTimerMs = 2_147_483_647;

interface DocumentPeriodReopenArgs {
  readonly documentId: number;
  readonly periodKey: number;
  /** Стан місяця з календаря проєкту (той самий для всіх документів). */
  readonly periodState: PeriodState | undefined;
}

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
export function useDocumentPeriodReopen({ documentId, periodKey, periodState }: DocumentPeriodReopenArgs): DocumentPeriodReopen {
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
    }, Math.min(Math.max(left, 0) + 1_000, MaxTimerMs));

    return () => clearTimeout(timer);
  }, [openUntil, documentId, queryClient]);

  const period = formatPeriodKey(periodKey);

  let lockAction: JSX.Element | null = null;
  if (state !== undefined && active === null) {
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

  const banner =
    active === null ? null : (
      <Alert
        color="brand"
        variant="light"
        role="status"
        title={t('document.reopen.activeTitle', { until: formatDateTime(active.openUntil) })}
        data-testid="document-period-reopened"
      >
        {t('document.reopen.activeBody', {
          period,
          reason: active.reason,
          by: active.openedByName ?? String(active.openedByUserId),
        })}
      </Alert>
    );

  const dialog =
    opened && state !== undefined ? (
      <Suspense fallback={null}>
        <DocumentPeriodReopenDialog
          documentId={documentId}
          periodKey={periodKey}
          defaultOpenUntil={state.defaultOpenUntil}
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
