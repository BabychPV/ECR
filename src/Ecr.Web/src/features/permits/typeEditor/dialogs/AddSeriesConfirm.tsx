import type { JSX } from 'react';
import type { PermitTypeColumn } from '@/features/permits/api/types';
import { t } from '@/shared/i18n';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import type { UnitOption } from './names';
import { MonthGroupCode, buildMonthColumns } from './ops';

/**
 * Підтвердження «Add 12 month columns?» (мокап `add-series`; Permits, PM-08c).
 *
 * Дванадцять числових стовпців січень…грудень у групі «Estimated volume by month» з однією
 * одиницею. Підтвердження, а не форма: усе вирішено заздалегідь, людина лише погоджується.
 *
 * ⚠ `unit === null` — у довіднику немає одиниці серії (`thousand_m3`): тоді діалог чесно каже, що
 * одиницю треба обрати для групи після додавання, а не підставляє першу-ліпшу.
 */
export interface AddSeriesConfirmProps {
  readonly opened: boolean;
  readonly tableCode: string;

  /** Коди стовпців таблиці, які вже є. */
  readonly existingCodes: readonly string[];

  /** Одиниця серії або `null`, якщо її в довіднику немає. */
  readonly unit: UnitOption | null;
  readonly onApply: (columns: PermitTypeColumn[]) => void;
  readonly onClose: () => void;
}

export function AddSeriesConfirm({
  opened,
  tableCode,
  existingCodes,
  unit,
  onApply,
  onClose,
}: AddSeriesConfirmProps): JSX.Element {
  const group = MonthGroupCode.replace(/_/g, ' ');

  return (
    <ConfirmModal
      opened={opened}
      title={t('permits.editor.dialogs.addSeries.title')}
      verb={t('permits.editor.dialogs.addSeries.verb')}
      danger={false}
      consequences={[
        t('permits.editor.dialogs.addSeries.months', { group }),
        {
          text:
            unit === null
              ? t('permits.editor.dialogs.addSeries.noUnit')
              : t('permits.editor.dialogs.addSeries.unit', { unit: unit.label }),
          note: true,
        },
        { text: t('permits.editor.dialogs.addSeries.checkHint'), note: true },
      ]}
      onConfirm={() => {
        onApply(
          buildMonthColumns({
            unitId: unit?.id ?? null,
            group: MonthGroupCode,
            existingCodes,
            startOrder: existingCodes.length + 1,
          }),
        );
        onClose();
      }}
      onClose={onClose}
      // ⚠ Таблиця — у тексті заголовка контексту, не в назві кнопки: дієслово лишається коротким.
      text={t('permits.editor.dialogs.addSeries.table', { table: tableCode })}
    />
  );
}
