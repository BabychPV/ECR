import type { RegistryEntryHistoryItem } from '@/features/registries/rows/api';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import type { RegistryField } from './rowModel';

/** Ключ кешу історії запису: під `['registries']`, тож правка в шторці її оновлює. */
export const entryHistoryKey = (code: string, entryId: number) =>
  ['registries', 'entryHistory', code, entryId] as const;

/**
 * Що змінилось — словами (RT-15, `kind`).
 *
 * ⚠ Для `value` — назва поля з опису довідника, а не код: людина бачить у сітці назви. Поля, якого
 * вже немає в описі (прибрали після правки), показуємо кодом — історія про нього лишається правдою.
 */
export function historyChangeLabel(item: RegistryEntryHistoryItem, fields: readonly RegistryField[]): string {
  switch (item.kind) {
    case 'value': {
      const field = fields.find((f) => f.code === item.field);
      return (field === undefined ? '' : localized(field.nameL10n)) || item.field || '';
    }
    case 'created':
    case 'name':
    case 'validity':
    case 'active':
    case 'deleted':
      return t(`registries.entryHistory.kind.${item.kind}`);
    default:
      // Новий вид зміни, якого клієнт ще не знає: код, а не `⟦…⟧`.
      return item.kind;
  }
}

/**
 * Значення «було» або «стало» для показу.
 *
 * ⛔ `display` має перевагу: для `Lookup` `value` — Id цілі, а людині потрібна назва; для `Unit` —
 * код одиниці. Числа — рядком як є: `Number(...)` зрізав би знаки.
 *
 * ⚠ `validity` — інтервал ISO 8601 `2026-01-01/2027-01-01`, відкритий кінець — `..`.
 */
export function historyValueText(item: RegistryEntryHistoryItem, side: 'old' | 'new'): string {
  const value = side === 'old' ? item.oldValue : item.newValue;
  const display = side === 'old' ? item.oldDisplay : item.newDisplay;

  if (value === null) return display ?? '';

  if (item.kind === 'validity') {
    const [from = '..', to = '..'] = value.split('/');
    const open = t('registries.entryHistory.openEnded');
    return `${from === '..' ? open : from} — ${to === '..' ? open : to}`;
  }

  if (item.kind === 'active') {
    if (value === 'true') return t('registries.entryHistory.yes');
    if (value === 'false') return t('registries.entryHistory.no');
  }

  return display ?? value;
}
