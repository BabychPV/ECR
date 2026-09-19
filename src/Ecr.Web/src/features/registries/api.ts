import { useMutation, useQueryClient, type UseMutationResult } from '@tanstack/react-query';
import { apiFetch, EcrApiError } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { CreateRegistryDto, RegistryDefDto } from '@/api/types';

/*
 * ⛔ Адреса в КОЖНІЙ функції записана повністю, а не збирається з помічника.
 * Сторож `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає в коді клієнта
 * саме літерали `/api/v1/…` разом із методом поруч; винесений у помічник
 * префікс зробив би дію «недосяжною з інтерфейсу» для сторожа.
 */

/**
 * Заводить довідник-контейнер — **без жодного поля** (директива №11, T4).
 *
 * ⛔ Поля заводяться окремою дією, тим самим конструктором, що вже редагує
 * наявний довідник (`ФВ-8.12`): перше поле майже завжди ключове, і це рішення
 * тут ще ніхто не ухвалив.
 */
export function createRegistry(body: CreateRegistryDto): Promise<RegistryDefDto> {
  return apiFetch<RegistryDefDto>('/api/v1/registries', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

/** Код відмови «на запис посилаються дані» (`ФВ-8.6`, `ФВ-8.7`). */
const ENTRY_IN_USE = 'ECR-REG-0409';

/**
 * Скільки комірок посилається на запис, який відмовилися видаляти.
 *
 * ⛔ Повертає `null`, а не `0`, коли відмова інша або лічильника в ній немає.
 * Нуль тут був би брехнею того самого роду, що «0 зауважень» у документа, який
 * ніколи не перевіряли: він стверджує факт, якого сервер не казав.
 */
export function entryReferences(error: unknown): number | null {
  if (!(error instanceof EcrApiError) || error.problem.errorCode !== ENTRY_IN_USE) {
    return null;
  }

  const references = error.problem.extensions2?.['references'];

  return typeof references === 'number' ? references : null;
}

/**
 * Видаляє запис довідника (`ФВ-8.6`, директива №15 `BE-01`).
 *
 * ⛔ Обробник на сервері існував і не мав в інтерфейсі жодного споживача:
 * видалити запис можна було лише руками в базі.
 *
 * ⚠ `onError` тут НЕМАЄ навмисно. Відмова `ECR-REG-0409` — не аварія, а
 * відповідь по суті: на запис посилаються дані, і правильна наступна дія —
 * закрити його датою (`POST …/validity`), а не повторити той самий запит.
 * Показати це має діалог, який знає про обидві дії; плашка «щось пішло не так»
 * із кнопкою «повторити» підказала б рівно те, що не спрацює.
 *
 * ⚠ `code` у шляху не декоративний: сервер звіряє належність запису довіднику
 * і на чужий відповідає `404`.
 */
export function useDeleteRegistryEntry(code: string): UseMutationResult<void, Error, number> {
  const queryClient = useQueryClient();

  return useMutation<void, Error, number>({
    mutationFn: (entryId: number) =>
      apiFetch<void>(
        `/api/v1/registries/${encodeURIComponent(code)}/entries/${entryId}`,
        { method: 'DELETE' },
      ),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.registries.entries(code) });
    },
  });
}
