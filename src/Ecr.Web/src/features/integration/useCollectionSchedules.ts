import { useQuery } from '@tanstack/react-query';
import { listCollectionSchedules } from '@/features/integration/scheduleApi';

/** Префікс ключів переліку розкладів збору — для інвалідизації всіх з'єднань разом. */
export const CollectionSchedulesKey = ['collection-schedules'] as const;

/**
 * Ключ переліку розкладів ОДНОГО з'єднання.
 *
 * ⛔ Код з'єднання — частина ключа, а не лише параметр запиту: зі спільним
 * ключем дві шухляди ділили б один запис кешу, і розклади одного з'єднання
 * показалися б під іншим, доки не прийде перечитування.
 */
export function collectionSchedulesKey(dataSource: string) {
  return [...CollectionSchedulesKey, dataSource] as const;
}

/**
 * Розклади збору одного з'єднання (сервер фільтрує `?dataSource=<code>`).
 *
 * ⛔ `refetchOnWindowFocus: false` — не косметика. Форма тримає `rowVersion`
 * рядка, який ПОКАЗАЛИ людині, і шле саме його в `If-Match`. Мовчазне
 * перечитування на фокусі вікна підмінило б показаний рядок свіжим, форма
 * перемонтувалася б із чужими значеннями — і людина зберегла б поверх чужої
 * правки, так і не побачивши її. Свіжу версію беруть свідомо: кнопкою
 * перечитування після конфлікту.
 */
export function useCollectionSchedules(dataSource: string) {
  return useQuery({
    queryKey: collectionSchedulesKey(dataSource),
    queryFn: () => listCollectionSchedules({ dataSource }),
    refetchOnWindowFocus: false,
  });
}
