import type { JSX } from 'react';
import { Text } from '@mantine/core';
import { t } from '@/shared/i18n';

/**
 * ✎ D-450 + live-прохід координатора 08.11 (P3): колонка «Owner» переліку документів.
 *
 * ⛔ Корінь «у службового облікового запису — «—» у кожному рядку»: це НАВМИСНЕ приховування R-7
 * (`DocumentSheetVisibility.For`): читач, якому в проєкті документа закрито хоч один аркуш (роль зі `scope` на
 * аркуші — такі в службових облікових записів), не бачить «хто й коли правив» і власника — інакше це розкривало б
 * редактора схованого аркуша. Сервер віддає тоді `ownerDisplayName = null` і `modifiedAt = null`; `modifiedAt` у
 * документа є ЗАВЖДИ (`Document.ModifiedAt` не nullable), тож його відсутність і є ознакою приховування.
 * Раніше клітинка показувала голе «—» з підказкою лише на наведення («Owner not shown») — без причини.
 * Тепер причину видно й з клавіатури (`tabIndex` + `aria-label`, підказка `title`): «прихована частина документа» або «облікового запису
 * власника немає».
 */
export function DocumentOwnerCell({
  ownerDisplayName,
  modifiedAt,
}: {
  readonly ownerDisplayName: string | null | undefined;
  readonly modifiedAt: string | null | undefined;
}): JSX.Element {
  if (typeof ownerDisplayName === 'string') {
    return (
      <Text size="sm" className="ecr-docs-owner" title={ownerDisplayName}>
        {ownerDisplayName}
      </Text>
    );
  }

  const hidden = modifiedAt === null || modifiedAt === undefined;
  const reason = hidden ? t('documents.ownerHidden') : t('documents.ownerUnknown');

  return (
    // ⚠ Не `Hint` (Popover): він додавав ~3 КБ до бюджету маршруту DocumentsPage (D-132). Причина — підказкою
    // (`title`) і для читалки (`aria-label`), у порядку табуляції.
    <Text span c="dimmed" tabIndex={0} title={reason} aria-label={reason} data-owner={hidden ? 'hidden' : 'unknown'}>
      —
    </Text>
  );
}
