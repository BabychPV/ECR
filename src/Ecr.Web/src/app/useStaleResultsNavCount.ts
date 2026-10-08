import { useDocumentListSummary } from '@/features/documents/api';
import { useUrlNumber } from '@/shared/ui/useUrlState';

/**
 * Скільки документів чекають перерахунку — число для пункту меню «Documents» (RC14-D).
 *
 * ⚠ Береться з того самого зведення, що й смуга над переліком (`useDocumentListSummary`, `staleResultsCount`;
 * сервер уже виключив проєкти зі звуженим доступом читача), тож додаткового запиту на екрані документів немає.
 * Період — з адреси (`?periodKey`): застарілість належить періоду, глобального «поточного» у меню немає, тому
 * поза екранами з періодом у адресі лічильника просто нема (`0`), а не вигадана цифра.
 * `enabled = false` (усі пункти, крім Documents) — запит не йде взагалі.
 */
export function useStaleResultsNavCount(enabled: boolean): number {
  const [periodKey] = useUrlNumber('periodKey');
  const summary = useDocumentListSummary(enabled ? periodKey : null);

  return enabled ? (summary.data?.staleResultsCount ?? 0) : 0;
}
