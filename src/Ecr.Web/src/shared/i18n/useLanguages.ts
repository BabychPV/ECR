import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { LanguageDto } from '@/api/types';

/**
 * Мови інтерфейсу з реєстру (`ФВ-14.9`).
 *
 * ⛔ Саме з сервера, а не константою в бандлі. Вимога каже: «додавання мови —
 * запис у реєстр, не збірка клієнта», і список `['en','ru','kz']` у коді
 * зробив би її невиконуваною — четверта мова з'явилася б у базі, у полях
 * назви її не було б, і причина була б невидима.
 *
 * ⚠ Кешується надовго: реєстр мов міняється раз на роки, а питати його на
 * кожне відкриття форми — це запит заради відповіді з трьох рядків.
 */
export function useLanguages() {
  return useQuery({
    queryKey: ['languages'],
    queryFn: () => apiFetch<LanguageDto[]>('/api/v1/languages'),
    staleTime: 60 * 60 * 1000,
  });
}
