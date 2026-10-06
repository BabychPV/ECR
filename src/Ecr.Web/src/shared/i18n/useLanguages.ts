import { useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { LanguageDto } from '@/api/types';
import { MeQueryKey, type MeDto } from '@/shared/session/useSession';

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
 *
 * ⛔ A3-02/A3 (приймальний прохід): під `mustChangePassword` сервер відповідає на все, крім зміни
 * пароля, `428`, тож меню профілю на екрані примусової зміни давало `428 GET /api/v1/languages`
 * (так само, як раніше `/jobs`, A2-06). Запит там не йде; перемикач без мов ховається сам.
 * ⚠ Профіль читається з кешу (`getQueryData`), а не через `useSession`: ще один спостерігач `/me`
 * із `staleTime: 0` перезапитував би профіль на кожне монтування.
 */
export function useLanguages() {
  const me = useQueryClient().getQueryData<MeDto>(MeQueryKey);

  return useQuery({
    queryKey: ['languages'],
    queryFn: () => apiFetch<LanguageDto[]>('/api/v1/languages'),
    staleTime: 60 * 60 * 1000,
    enabled: me?.mustChangePassword !== true,
  });
}
