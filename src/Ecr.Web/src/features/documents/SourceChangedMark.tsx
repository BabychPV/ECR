import type { JSX } from 'react';
import { Badge } from '@mantine/core';
import { formatPeriodKey } from '@/shared/format/periodKey';
import { t } from '@/shared/i18n';
import { Hint } from '@/shared/ui/Hint';
import { toneFills } from '@/shared/ui/StatusBadge';

interface SourceChangedMarkProps {
  /** Затверджені періоди документа (`yyyyMM`), чиє джерело змінилося після затвердження (`sourceChangedPeriods`). */
  readonly periods: readonly number[];
}

/**
 * ✎ F5-R (D-570, рішення людини P-1122-4: «так познач щоб користувач бачив це»): позначка рядка переліку — документ-джерело
 * (склад HSE400) змінився ПІСЛЯ затвердження місяця, а затверджений місяць каскад не переписує, тож числа можуть бути
 * застарілими. Пояснення й дія — у підказці (переглянути / повернути в роботу / перерахувати «з затвердженими», B80 N5).
 *
 * ⚠ Та сама форма, що `LateEditsMark`: `<Hint focusable>` (клавіатура, дотик, читалка), підпис — носій змісту, колір
 * «увага» з `toneFills` — другий носій (`ФВ-14.18`).
 */
export function SourceChangedMark({ periods }: SourceChangedMarkProps): JSX.Element | null {
  if (periods.length === 0) return null;
  const fill = toneFills.warning;
  const months = periods.map((period) => formatPeriodKey(period) || String(period)).join(', ');

  return (
    <Hint label={t('documents.sourceChangedHint', { months })} focusable>
      <Badge
        size="sm"
        miw="fit-content"
        variant="transparent"
        c={fill.text}
        styles={{ root: { textTransform: 'none', letterSpacing: 'normal', fontWeight: 500, paddingInline: 0 } }}
        data-source-changed="true"
        data-periods={periods.join(',')}
      >
        {t('documents.sourceChanged')}
      </Badge>
    </Hint>
  );
}
