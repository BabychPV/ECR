import type { JSX } from 'react';
import { useQuery } from '@tanstack/react-query';
import { getPermitTypeUsage } from '@/features/permits/api/typesApi';
import type { PermitType } from '@/features/permits/api/types';
import { t } from '@/shared/i18n';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { hasPublishedVersion } from '../../lib/typeStatus';
import { findTable, referencesTo, type TypeDraft } from '../draftModel';
import { typeEditorKeys } from '../typeEditorKeys';
import { pickName } from './names';

/**
 * Підтвердження «Remove table» (Permits, зведення PM-08b + PM-08c, кількості — PM-03b2). Видалення таблиці —
 * правка робочої копії (`removeTable`). Що в діалозі, залежить від стану:
 *
 * 1. на таблицю посилається список іншої таблиці — не можна, діалог називає стовпці (кількості не питаємо);
 * 2. тип ніколи не публікувався або таблиця додана в цій чернетці й не збережена — дані в ній не можуть бути:
 *    звичайне підтвердження, запиту немає;
 * 3. тип має опубліковану версію, а таблиця вже збережена — клієнт питає `GET …/usage` (кількості ОПУБЛІКОВАНИХ
 *    таблиць) і, доки відповіді немає, кнопка вимкнена («Checking…»):
 *    a. таблиця опублікована — не можна: сервер відхилив би збереження `422 ECR-PRM-0422 tableRemoved`, тож
 *       показуємо ТОЙ САМИЙ текст каталогу + скільки дозволів і рядків лишаються недоторканими;
 *    b. таблиці немає серед опублікованих (її додано в чернетці нової версії) — можна: звичайне підтвердження
 *       і примітка, що жоден дозвіл не тримає в ній рядків;
 *    c. запит не вдався — діалог поводиться як до PM-03b2 (збережену таблицю опублікованого типу не видалити):
 *       без кількостей, але й без зайвої обіцянки.
 *
 * ⚠ Рішення «можна/не можна» в 3a/3b узяте з того, що знає сервер (склад опублікованої версії), а не з
 * припущення клієнта; правило сервера (`ValidateAgainstPublished`) не змінювалося.
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
  const columns = table === undefined ? [] : referencesTo(draft, table.code).map((ref) => `${ref.tableCode}.${ref.columnCode}`);
  const saved = table !== undefined && type.tables.some((entry) => entry.code.toLowerCase() === table.code.toLowerCase());
  const asksUsage = table !== undefined && columns.length === 0 && saved && hasPublishedVersion(type);

  // Свіжий запит на кожне відкриття: число з кешу минулого діалогу могло встигнути застаріти.
  const usage = useQuery({
    queryKey: typeEditorKeys.usage(type.code),
    queryFn: () => getPermitTypeUsage(type.code),
    enabled: asksUsage,
    staleTime: 0,
    gcTime: 0,
    refetchOnWindowFocus: false,
    retry: false,
  });

  if (table === undefined) return null;

  const name = pickName(table.nameL10n) || table.code;
  const blockedText = `${t('err.ECR-PRM-0422.tableRemoved', { tableCode: table.code })} ${t('permits.editor.deleteTable.published')}`;
  const confirmText = t('permits.editor.deleteTable.confirm', { name });

  let text: JSX.Element | string = confirmText;
  let blocked = false;
  let note: string | undefined;
  // Кількості опублікованої таблиці — суть відмови (звичайний рядок); «нічого не втрачається» — лише уточнення.
  let dim = true;

  if (columns.length > 0) {
    text = t('permits.editor.deleteTable.referenced', { name, columns: columns.join(', ') });
    blocked = true;
  } else if (asksUsage) {
    if (usage.isPending) {
      text = <span role="status">{t('permits.editor.deleteTable.checking')}</span>;
      blocked = true;
    } else if (usage.isError) {
      text = blockedText;
      blocked = true;
    } else {
      const entry = usage.data.tables.find((row) => row.code.toLowerCase() === table.code.toLowerCase());
      if (entry === undefined) {
        note = t('permits.editor.deleteTable.usageDraftOnly', { permits: usage.data.instanceCount });
      } else {
        text = blockedText;
        blocked = true;
        note = t('permits.editor.deleteTable.usagePublished', { permits: usage.data.instanceCount, rows: entry.rowCount });
        dim = false;
      }
    }
  }

  return (
    <ConfirmModal
      opened
      title={t('permits.editor.deleteTable.title', { name })}
      text={text}
      {...(note === undefined ? {} : { consequences: [{ text: note, note: dim }] })}
      verb={t('permits.editor.deleteTable.verb')}
      confirmDisabled={blocked}
      onConfirm={() => {
        onRemove(table.code);
        onClose();
      }}
      onClose={onClose}
    />
  );
}
