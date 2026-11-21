import { formatNumber } from '@/shared/format';
import { t } from '@/shared/i18n';

/** Перелік, прочитаний усіма сторінками (`fetchAllPages`): `nextCursor` не `null` — спрацював запобіжник. */
export interface ReadList {
  readonly items: readonly unknown[];
  readonly nextCursor: string | null;
}

/** Значення пункту-примітки у виборі; людина обрати його не може (`disabled`). */
export const TruncatedOptionValue = '__list-truncated__';

/** Чи дочитано перелік лише до запобіжника. */
export function isTruncated(list: ReadList | undefined): list is ReadList {
  return list !== undefined && list.nextCursor !== null;
}

/**
 * «Показано перші N; є ще» — останнім НЕАКТИВНИМ пунктом випадного списку (PSS-19c, рецензія L4): людина бачить
 * примітку рівно там, де шукає пункт. Порожньо, якщо перелік повний.
 *
 * ⚠ Окремим `.ts` без компонентів (рецензія L10, M3): вибір автора журналу (`authorOptions`) живе на маршруті
 * `AuditPage` біля межі бюджету `D-132`, і тягнути йому `Text` заради рядка підпису нема чого.
 */
export function truncatedOption(list: ReadList | undefined): { value: string; label: string; disabled: true }[] {
  if (!isTruncated(list)) return [];

  return [
    {
      value: TruncatedOptionValue,
      label: t('common.shownSoFar', { shown: formatNumber(list.items.length) }),
      disabled: true,
    },
  ];
}
