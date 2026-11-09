import { useState, type JSX } from 'react';
import { Anchor, Button, Group, List, Stack, Text } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { queryKeys } from '@/api/queryKeys';
import {
  closeDocumentPeriodReopen,
  periodReopenKey,
  projectDocumentReopensKey,
  type ProjectDocumentReopenItem,
} from '@/features/documents/periodReopen/api';
import { formatCount } from '@/shared/format/plural';
import { t } from '@/shared/i18n';
import { siteMomentText } from '@/shared/siteMoment';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { showApiError, showDone } from '@/shared/ui/notify';

interface PeriodReopenedDocumentsProps {
  readonly projectId: number;
  readonly periodKey: number;
  /** Підпис місяця («January 2026») — для заголовка підтвердження й повідомлення. */
  readonly periodCaption: string;
  /** Пояс майданчика (IANA): строк показується в ньому, як і решта моментів календаря. */
  readonly timeZoneId: string;
  /** Діючі винятки ЦЬОГО місяця (лише видимі документи — фільтрує сервер). */
  readonly items: readonly ProjectDocumentReopenItem[];
  /** Чи може користувач закрити виняток (`Period.Reopen` у проєкті, рішення сервера). */
  readonly canClose: boolean;
}

/** Момент у поясі майданчика з годиною; нерозібраний — як є. */
function siteTime(value: string, zone: string): string {
  return siteMomentText(value, zone) ?? value;
}

/**
 * «Locked · N document(s) reopened until …» для винятків ОДНОГО місяця (строк — найпізніший, у поясі майданчика);
 * `null` — винятків немає. Той самий рядок — у календарі (нижче) і на плитці року (`PeriodYearTiles`, debt-1109 i4-P3).
 */
export function reopenedSummaryText(
  items: readonly Pick<ProjectDocumentReopenItem, 'openUntil'>[],
  timeZoneId: string,
): string | null {
  if (items.length === 0) return null;

  const latest = items.reduce((max, item) => (Date.parse(item.openUntil) > Date.parse(max) ? item.openUntil : max), items[0]?.openUntil ?? '');

  return formatCount(items.length, 'periods.reopenedDocs.summary', { until: siteTime(latest, timeZoneId) });
}

/**
 * Приймальна i4: закритий місяць, відкритий для окремих документів, — «Locked · 1 document reopened until …» з
 * номерами документів і дією «Close now» на вкладці Periods.
 *
 * ⚠ Множина — категоріями каталогу (`formatCount`), ніколи «1 documents». Строк — найпізніший серед документів місяця
 * (до нього місяць ще не закритий для всіх), у поясі МАЙДАНЧИКА (`siteMoment`), як і «Locks on».
 *
 * ⛔ Право й видимість вирішує сервер: схований документ у перелік не потрапляє; без `Period.Reopen` кнопка
 * недоступна, і причина — словами поруч, як у банері документа (#782, i6).
 */
export function PeriodReopenedDocuments({
  projectId,
  periodKey,
  periodCaption,
  timeZoneId,
  items,
  canClose,
}: PeriodReopenedDocumentsProps): JSX.Element | null {
  const queryClient = useQueryClient();
  const [closing, setClosing] = useState<ProjectDocumentReopenItem | null>(null);

  const close = useMutation({
    mutationFn: (item: ProjectDocumentReopenItem) => closeDocumentPeriodReopen(item.documentId, item.periodKey),
    onSuccess: async (_, item) => {
      // Перелік винятків (під префіксом календаря) і календар, стан винятку й документ (#782) — рядок знову «Locked».
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: projectDocumentReopensKey(projectId) }),
        queryClient.invalidateQueries({ queryKey: ['periods', projectId] }),
        queryClient.invalidateQueries({ queryKey: periodReopenKey(item.documentId, item.periodKey) }),
        queryClient.invalidateQueries({ queryKey: queryKeys.document.ofDocument(item.documentId) }),
        queryClient.invalidateQueries({ queryKey: queryKeys.slices.all() }),
      ]);
      setClosing(null);
      showDone(t('periods.reopenedDocs.closed', { period: periodCaption, document: item.documentName }));
    },
    onError: showApiError,
  });

  const summary = reopenedSummaryText(items, timeZoneId);
  if (summary === null) return null;

  return (
    // ✎ debt-1109 i4-P3: без верхнього відступу — бейдж «Locked» над рядком сторінка вже не малює (рядок сам каже «Locked»).
    <Stack gap="xs" data-period-reopened-docs={periodKey}>
      <Text size="sm" fw={600} data-testid="period-reopened-summary">
        {summary}
      </Text>
      <List size="sm" spacing="xs" listStyleType="none" aria-label={t('periods.reopenedDocs.listLabel', { period: periodCaption })}>
        {items.map((item) => (
          <List.Item key={item.id} data-period-reopened-doc={item.documentId}>
            <Group gap="xs" wrap="wrap">
              <Anchor component={Link} to={`/documents/${String(item.documentId)}?periodKey=${String(item.periodKey)}`} size="sm">
                {item.documentName}
              </Anchor>
              {items.length > 1 && (
                <Text span size="xs" c="dimmed">
                  {t('periods.reopenedDocs.itemUntil', { until: siteTime(item.openUntil, timeZoneId) })}
                </Text>
              )}
              <Button
                size="xs"
                variant="light"
                disabled={!canClose}
                onClick={() => setClosing(item)}
                data-testid="period-reopened-close"
              >
                {t('periods.reopenedDocs.closeNow')}
              </Button>
            </Group>
          </List.Item>
        ))}
      </List>
      {!canClose && (
        <Text size="xs" c="dimmed" data-testid="period-reopened-denied">
          {t('periods.reopenedDocs.noPermission')}
        </Text>
      )}

      <ConfirmModal
        opened={closing !== null}
        title={t('periods.reopenedDocs.confirmTitle', { period: periodCaption, document: closing?.documentName ?? '' })}
        text={t('periods.reopenedDocs.confirmText', {
          until: closing === null ? '' : siteTime(closing.openUntil, timeZoneId),
        })}
        verb={t('periods.reopenedDocs.closeNow')}
        isPending={close.isPending}
        onConfirm={() => {
          if (closing !== null) close.mutate(closing);
        }}
        onClose={() => setClosing(null)}
      />
    </Stack>
  );
}
