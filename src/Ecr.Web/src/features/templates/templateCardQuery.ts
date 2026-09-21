import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import type { TemplateCard } from '@/api/types';
import { fetchTemplateCard } from './templateApi';

/**
 * Читання картки шаблону як ЗАПИТУ (`UI-09`).
 *
 * ⛔ `templateApi.ts` дає транспорт (`fetchTemplateCard`) і нічого більше —
 * ключа кешу в ньому немає. Класти `useQuery` прямо на сторінці означало б, що
 * ключ цієї картки існує рівно в одному виразі всередині JSX-файлу: наступний
 * споживач (прогрів за наміром, breadcrumb, інвалідація після дії) написав би
 * свій — і розійшовся б із цим непомітно, бо обидва виглядали б правдоподібно.
 *
 * ⚠ Ключ ПОХІДНИЙ від фабрики (`queryKeys.templates.all()`), а не набраний
 * рядком: `invalidateQueries({ queryKey: templates.all() })` мусить змітати й
 * картку теж. Власний домен (`['templateCard', id]`) пережив би таку
 * інвалідацію й лишився б показувати стару назву після перейменування — рівно
 * той тихий розлад, проти якого фабрика й існує (`queryKeys.ts`).
 *
 * ⚠ Фабрику не розширено НОВИМ методом навмисно: `api/queryKeys.ts` —
 * спільний ресурс, а ця картка змінює лише свої файли. Форма ключа лишається
 * сумісною з доменом, тож перенести її у фабрику потім можна без зміни
 * поведінки.
 */
export function templateCardKey(templateId: number): readonly unknown[] {
  return [...queryKeys.templates.all(), 'card', templateId];
}

/**
 * Картка шаблону разом із лічильником залежних.
 *
 * ⛔ `enabled` — не оптимізація. Без нього сторінка, відкрита без параметра
 * (`useParams()` порожній: прямий рендер у наборі доступності, помилка в
 * адресі), пішла б запитом на `/api/v1/templates/NaN` і показала б відмову
 * сервера там, де насправді немає самого питання.
 */
export function useTemplateCard(templateId: number): UseQueryResult<TemplateCard> {
  return useQuery({
    queryKey: templateCardKey(templateId),
    queryFn: () => fetchTemplateCard(templateId),
    enabled: Number.isFinite(templateId),
  });
}
