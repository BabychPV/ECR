import type { Permit } from '../api/types';

/**
 * Що можна робити з дозволом у його стані (мокап `PERMIT_SCREEN`, README §2): сервер перевіряє те саме
 * (`ECR-PRM-4096`), клієнт лише не показує кнопку, яка гарантовано дасть відмову.
 */
export const PermitStatus = { Draft: 'Draft', Active: 'Active', Expired: 'Expired' } as const;

/** За скільки днів до кінця чинного дозволу екран попереджає (мокап: «ending within 120 days»). */
export const ExpiringWithinDays = 120;

export interface PermitCapabilities {
  /** Шапку можна змінити: чернетка й чинний (сервер: закінчений — `notEditable`). */
  readonly editHeader: boolean;
  /**
   * Рядки таблиць можна змінювати.
   *
   * ⚠ Це правило ЛИШЕ інтерфейсу: сервер записів у таблиці закінченого дозволу не блокує (`permitsApi.ts`). Тому
   * закінчений дозвіл тут читається, а не змінюється, хоча API це дозволило б.
   */
  readonly editRows: boolean;
  readonly activate: boolean;
  readonly end: boolean;
  readonly renew: boolean;
  readonly remove: boolean;
}

/** Можливості за станом; без права на запис усе, що пише, вимкнено. */
export function capabilities(status: string, mayEdit: boolean): PermitCapabilities {
  const draft = status === PermitStatus.Draft;
  const active = status === PermitStatus.Active;
  const expired = status === PermitStatus.Expired;

  return {
    editHeader: mayEdit && (draft || active),
    editRows: mayEdit && !expired,
    activate: mayEdit && draft,
    end: mayEdit && active,
    renew: mayEdit && (active || expired),
    remove: mayEdit && draft,
  };
}

/** Чинний дозвіл, що скоро закінчується: скільки днів лишилось, інакше `null`. */
export function expiringIn(permit: Pick<Permit, 'status' | 'endsInDays'>): number | null {
  if (permit.status !== PermitStatus.Active || permit.endsInDays === null) return null;
  return permit.endsInDays <= ExpiringWithinDays ? permit.endsInDays : null;
}
