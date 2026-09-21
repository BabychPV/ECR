import type { JSX } from 'react';
import { Badge } from '@mantine/core';
import { t } from '@/shared/i18n';
import { toneFills } from '@/shared/ui/StatusBadge';

/**
 * Позначка «були пізні правки» в рядку переліку (`BE-09b`, `hasLateEdits`).
 *
 * ⚠ Текстом, без `title`-підказки: спільного доступного компонента підказки в
 * клієнті ще немає, а `title` недосяжний з клавіатури й дотиком. Підпис сам
 * несе зміст — колір лише другий носій (`ФВ-14.18`).
 *
 * ⚠ Тон `warning` і пара «текст на тлі» — з `toneFills`, тієї ж таблиці, що в
 * `<StatusBadge>`: ця пара вже виміряна на контраст в обох темах.
 */
export function LateEditsMark(): JSX.Element {
  const fill = toneFills.warning;

  return (
    <Badge size="sm" miw="fit-content" variant="default" bg={fill.bg} c={fill.text} data-late-edits="true">
      {t('documents.lateEdits')}
    </Badge>
  );
}
