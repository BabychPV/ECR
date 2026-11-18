import type { JSX } from 'react';
import { List, Text } from '@mantine/core';
import { formatPeriodKey } from '@/shared/format/periodKey';
import { t } from '@/shared/i18n';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import type { ApprovalRouteImpactDto } from './approvalRoutesApi';

/**
 * ✎ B155 (варіант C): підтвердження «N поданих аркушів почнуть погодження з першого кроку».
 *
 * ⚠ `total` — усі такі аркуші, `visible` — лише в проєктах, які бачить викликач, `sheets` — видимі (не більше 200,
 * `truncated`). Різниця `total − visible` названа окремо: інакше людина, що бачить два аркуші, підтвердила б
 * перезапуск двадцяти.
 */
export function RestartConfirm({
  impact,
  isPending,
  onConfirm,
  onClose,
}: {
  readonly impact: ApprovalRouteImpactDto | null;
  readonly isPending: boolean;
  readonly onConfirm: () => void;
  readonly onClose: () => void;
}): JSX.Element {
  const total = impact?.total ?? 0;
  const visible = impact?.visible ?? 0;
  const hidden = Math.max(0, total - visible);
  const sheets = impact?.sheets ?? [];

  const consequences = [
    t('approvalRoutes.restartVisible', { visible, total }),
    ...(hidden > 0 ? [t('approvalRoutes.restartHidden', { hidden })] : []),
    ...(impact?.truncated === true ? [{ text: t('approvalRoutes.restartTruncated', { count: sheets.length }), note: true }] : []),
  ];

  return (
    <ConfirmModal
      opened={impact !== null}
      title={t('approvalRoutes.restartTitle', { total })}
      text={t('approvalRoutes.restartText')}
      consequences={consequences}
      verb={t('approvalRoutes.restartVerb')}
      isPending={isPending}
      onConfirm={onConfirm}
      onClose={onClose}
    >
      {sheets.length > 0 ? (
        <List size="sm" spacing="xs" data-testid="approval-route-restart-sheets" aria-label={t('approvalRoutes.restartListLabel')}>
          {sheets.map((sheet) => (
            <List.Item key={`${String(sheet.documentId)}:${String(sheet.sheetDefId)}:${String(sheet.sheetCopyId ?? 0)}:${String(sheet.periodKey)}`}>
              <Text size="sm">
                {t(sheet.sheetCopyId === null ? 'approvalRoutes.restartSheet' : 'approvalRoutes.restartSheetCopy', {
                  project: sheet.projectId,
                  document: sheet.documentId,
                  sheet: sheet.sheetDefId,
                  copy: sheet.sheetCopyId ?? 0,
                  period: formatPeriodKey(sheet.periodKey) || String(sheet.periodKey),
                })}
              </Text>
            </List.Item>
          ))}
        </List>
      ) : null}
    </ConfirmModal>
  );
}
