import type { JSX } from 'react';
import { Badge } from '@mantine/core';
import { t } from '@/shared/i18n';
import { Hint } from '@/shared/ui/Hint';
import { toneFills } from '@/shared/ui/StatusBadge';

/**
 * Позначка «були пізні правки» в рядку переліку (`BE-09b`, `hasLateEdits`).
 *
 * ⚠ Пояснення — через `<Hint focusable>`, а не `title`: `title` недосяжний з
 * клавіатури й дотиком. Бейдж сам не фокусований, тож `focusable` ставить його
 * в порядок табуляції; пояснення доходить і до читалки (`aria-describedby`).
 * Підпис лишається носієм змісту, колір — лише другий носій (`ФВ-14.18`).
 *
 * ⚠ Тон `warning` і пара «текст на тлі» — з `toneFills`, тієї ж таблиці, що в
 * `<StatusBadge>`: ця пара вже виміряна на контраст в обох темах.
 */
export function LateEditsMark(): JSX.Element {
  const fill = toneFills.warning;

  return (
    <Hint label={t('documents.lateEditsHint')} focusable>
      {/* ✎ `UI-19`: за макетом — слово кольором «увага» поруч із часом
          (`.work-late`), а не капітельний бейдж у рамці. */}
      <Badge
        size="sm"
        miw="fit-content"
        variant="transparent"
        c={fill.text}
        styles={{ root: { textTransform: 'none', letterSpacing: 'normal', fontWeight: 500, paddingInline: 0 } }}
        data-late-edits="true"
      >
        {t('documents.lateEdits')}
      </Badge>
    </Hint>
  );
}
