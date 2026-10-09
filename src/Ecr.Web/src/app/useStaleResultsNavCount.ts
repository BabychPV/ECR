import { useDocumentListSummary } from '@/features/documents/api';
import { useUrlNumber } from '@/shared/ui/useUrlState';

/** Політика свіжості бейджа меню (AN-108 / P2-01). */
export const NavCountPolicy = { staleTime: 60_000, refetchOnWindowFocus: false } as const;

/**
 * Скільки документів чекають перерахунку — число для пункту меню «Documents» (RC14-D).
 *
 * ⚠ Береться з того самого зведення, що й смуга над переліком (`useDocumentListSummary`, `staleResultsCount`;
 * сервер уже виключив проєкти зі звуженим доступом читача), тож додаткового запиту на екрані документів немає.
 * Період — з адреси (`?periodKey`): застарілість належить періоду, глобального «поточного» у меню немає, тому
 * поза екранами з періодом у адресі лічильника просто нема (`0`), а не вигадана цифра.
 * `enabled = false` (усі пункти, крім Documents) — запит не йде взагалі.
 *
 * ⛔ AN-108 / P2-01: спостерігач меню змонтований на КОЖНОМУ екрані з `?periodKey`, зокрема на сторінці документа,
 * де оператор годинами вводить числа. Власна політика (`NavCountPolicy`): хвилина свіжості й без перезапиту на
 * фокус — бейдж є підказкою, а зведення — найдорожчий запит переліку.
 */
export function useStaleResultsNavCount(enabled: boolean): number {
  const [periodKey] = useUrlNumber('periodKey');
  const summary = useDocumentListSummary(enabled ? periodKey : null, NavCountPolicy);

  return enabled ? (summary.data?.staleResultsCount ?? 0) : 0;
}
