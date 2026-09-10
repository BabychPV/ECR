import { apiFetch } from '@/api/client';
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
