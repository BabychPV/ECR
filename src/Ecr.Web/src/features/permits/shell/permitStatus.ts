import { t } from '@/shared/i18n';
import type { StatusTone } from '@/shared/ui/StatusBadge';

/**
 * Стан екземпляра перміта (README макета §2): `Draft → Active → Expired` поверх `ValidFrom/ValidTo`.
 *
 * ⛔ Через `StatusBadge` це не виходить, і `StatusBadge` не редагується: його підпис береться з
 * каталогу за парою `status.<kind>.<state>` без перевизначення, а стани `version`
 * (`Draft/Published/Deprecated`) малювали б «Published» там, де макет каже «Active», і
 * «Deprecated» замість «Expired». Тому тон — з того самого набору (`StatusTone`, його ж
 * `badgeLook` читає `PermitStateBadge`), а підпис — власні ключі `permits.state.*`.
 */
export const permitStates = ['Draft', 'Active', 'Expired'] as const;

export type PermitState = (typeof permitStates)[number];

export function isPermitState(value: string): value is PermitState {
  return (permitStates as readonly string[]).includes(value);
}

/**
 * Тон за станом. Макет: `Active → Published`, `Draft → Draft`, `Expired → Archived` у `version`,
 * тобто нормальні стани нейтральні, завершений — бляклий (`KIT.md` §1.3: колір лише для того, що
 * не так). Невідомий стан сервера — `warning`, як і в `StatusBadge` (`UnknownStateTone`).
 */
export function permitStateTone(state: string): StatusTone {
  switch (state) {
    case 'Draft':
    case 'Active':
      return 'neutral';
    case 'Expired':
      return 'muted';
    default:
      return 'warning';
  }
}

/** Підпис стану. Невідомий стан показується як є: порожній бейдж нічого не пояснює. */
export function permitStateLabel(state: string): string {
  switch (state) {
    case 'Draft':
      return t('permits.state.Draft');
    case 'Active':
      return t('permits.state.Active');
    case 'Expired':
      return t('permits.state.Expired');
    default:
      return state;
  }
}

/** Індекс стану на лінії Draft → Active → Expired; `-1` для невідомого. */
export function permitStateStep(state: string): number {
  return (permitStates as readonly string[]).indexOf(state);
}
