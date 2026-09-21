import { useQuery } from '@tanstack/react-query';
import { listCollectionSchedules } from '@/features/integration/scheduleApi';

/** Ключ переліку розкладів збору. */
export const CollectionSchedulesKey = ['collection-schedules'] as const;

/**
 * Перелік розкладів збору.
 *
 * ⛔ `refetchOnWindowFocus: false` — не косметика. Форма тримає `rowVersion`
 * рядка, який ПОКАЗАЛИ людині, і шле саме його в `If-Match`. Мовчазне
 * перечитування на фокусі вікна підмінило б показаний рядок свіжим, форма
 * перемонтувалася б із чужими значеннями — і людина зберегла б поверх чужої
 * правки, так і не побачивши її. Свіжу версію беруть свідомо: кнопкою
 * перечитування після конфлікту.
 */
export function useCollectionSchedules() {
  return useQuery({
    queryKey: CollectionSchedulesKey,
    queryFn: listCollectionSchedules,
    refetchOnWindowFocus: false,
  });
}
