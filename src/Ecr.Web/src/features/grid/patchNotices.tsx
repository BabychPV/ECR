import type { JSX } from 'react';
import { Alert, List } from '@mantine/core';
import type { PatchCellsResponse } from '@/api/types';
import { t } from '@/shared/i18n';

/**
 * Зауваження `Warning`/`Info`, які сервер повертає в УСПІШНІЙ відповіді
 * `PATCH` комірок (`PatchCellsResponse.validation`, R-B3).
 *
 * ⛔ До цього сітка брала з `validation` лише `ECR-CALC-0437` (обов'язкові
 * вхідні колонки), а решту відкидала мовчки: правило рівня `Warning` чи
 * `Info` спрацьовувало, запис проходив — і оператор про зауваження не
 * дізнавався до перевірки документа.
 *
 * ⚠ Як і `requiredInputWarnings`, зауваження живуть по РЯДКАХ: новий патч
 * замінює зауваження своїх рядків і рівня таблиці (`rowKey = null`), а чужих
 * рядків не чіпає.
 */
export interface PatchNotice {
  readonly rowKey: string | null;
  readonly columnCode: string | null;
  readonly ruleCode: string;
  readonly severity: 'Warning' | 'Info';
  readonly message: string;
}

/** Коди, які сітка показує власним блоком (`requiredInputWarnings`). */
const OwnBlockRuleCodes = new Set(['ECR-CALC-0437']);

export function mergePatchNotices(
  previous: readonly PatchNotice[],
  touchedRowKeys: ReadonlySet<string>,
  validation: PatchCellsResponse['validation'],
): readonly PatchNotice[] {
  const kept = previous.filter((notice) => notice.rowKey !== null && !touchedRowKeys.has(notice.rowKey));
  const fresh = validation
    .filter((m) => !OwnBlockRuleCodes.has(m.ruleCode) && (m.severity === 'Warning' || m.severity === 'Info'))
    .map(
      (m): PatchNotice => ({
        rowKey: m.rowKey,
        columnCode: m.columnCode,
        ruleCode: m.ruleCode,
        severity: m.severity === 'Warning' ? 'Warning' : 'Info',
        message: m.message,
      }),
    );

  return [...kept, ...fresh];
}

interface PatchNoticesAlertProps {
  readonly notices: readonly PatchNotice[];
  readonly onDismiss: () => void;
}

/**
 * ⚠ Та сама розкладка, що й у блоку «обов'язкові вхідні» (`Alert`, роль
 * `alert` Mantine): блок з'являється лише після патчу з зауваженнями.
 */
export function PatchNoticesAlert({ notices, onDismiss }: PatchNoticesAlertProps): JSX.Element | null {
  if (notices.length === 0) return null;

  const warning = notices.some((notice) => notice.severity === 'Warning');

  return (
    <Alert
      {...(warning ? { color: 'statusWarning' } : {})}
      title={t('grid.patchNoticesTitle', { count: notices.length })}
      withCloseButton
      closeButtonLabel={t('common.close')}
      onClose={onDismiss}
      data-patch-notices={warning ? 'Warning' : 'Info'}
    >
      <List size="sm">
        {notices.map((notice, index) => (
          <List.Item key={`${notice.ruleCode}:${notice.rowKey ?? ''}:${notice.columnCode ?? ''}:${String(index)}`}>
            {notice.message}
          </List.Item>
        ))}
      </List>
    </Alert>
  );
}
