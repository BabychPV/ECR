import type { JSX } from 'react';
import type { PermitType } from '@/features/permits/api/types';
import { t } from '@/shared/i18n';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { findTable, referencesTo, type TypeDraft } from '../draftModel';
import { pickName } from './names';

/**
 * Підтвердження «Remove table» (Permits, зведення PM-08b + PM-08c). Видалення таблиці — правка
 * робочої копії (`removeTable`), тож вирішує, чи можна, ЛИШЕ те, що відомо клієнту:
 *
 * - на таблицю посилається список іншої таблиці — не можна, діалог називає стовпці;
 * - тип має опубліковані версії, а таблиця вже збережена на сервері — не можна: сервер відхилив би
 *   збереження `422 ECR-PRM-0422 tableRemoved` («Table … is published and cannot be removed»), тож
 *   показуємо ТОЙ САМИЙ текст каталогу заздалегідь, а не приречену кнопку;
 * - решта (тип ще не публікувався або таблиця додана в цій чернетці й не збережена) — звичайне
 *   підтвердження.
 *
 * ⚠ Тип, який мав опубліковану версію, а таблицю зберіг у чернетці й не публікував, клієнт від
 * опублікованої не відрізнить (сервер не віддає склад опублікованої версії) — такі таблиці теж
 * блокуються. Це відома межа, не прихований стан.
 */
export interface DeleteTableDialogProps {
  /** Те, що знає сервер: `version` і збережений склад. */
  readonly type: PermitType;
  /** Робоча копія (з правками, яких сервер ще не бачив). */
  readonly draft: TypeDraft;
  readonly tableCode: string;
  readonly onRemove: (tableCode: string) => void;
  readonly onClose: () => void;
}

export function DeleteTableDialog({ type, draft, tableCode, onRemove, onClose }: DeleteTableDialogProps): JSX.Element | null {
  const table = findTable(draft, tableCode);
  if (table === undefined) return null;

  const name = pickName(table.nameL10n) || table.code;
  const columns = referencesTo(draft, table.code).map((ref) => `${ref.tableCode}.${ref.columnCode}`);
  const saved = type.tables.some((entry) => entry.code.toLowerCase() === table.code.toLowerCase());
  const publishedBlock = type.version > 0 && saved;

  const text =
    columns.length > 0
      ? t('permits.editor.deleteTable.referenced', { name, columns: columns.join(', ') })
      : publishedBlock
        ? `${t('err.ECR-PRM-0422.tableRemoved', { tableCode: table.code })} ${t('permits.editor.deleteTable.published')}`
        : t('permits.editor.deleteTable.confirm', { name });

  return (
    <ConfirmModal
      opened
      title={t('permits.editor.deleteTable.title', { name })}
      text={text}
      verb={t('permits.editor.deleteTable.verb')}
      confirmDisabled={columns.length > 0 || publishedBlock}
      onConfirm={() => {
        onRemove(table.code);
        onClose();
      }}
      onClose={onClose}
    />
  );
}
