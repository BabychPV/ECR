import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { components } from '@/api/schema';

export type EntryReferences = components['schemas']['RegistryEntryReferencesDto'];
export type EntryReference = components['schemas']['RegistryEntryReferenceDto'];

/**
 * «Де використовується» запис довідника (NAE plan N2): дозволи, що на нього посилаються.
 *
 * ⚠ Адреса записана повністю, а не збирається з помічника: сторож `Кожна_дія_сервера_має_споживача_в_інтерфейсі`
 * шукає літерал `/api/v1/…` разом із методом поруч.
 */
export function getEntryReferences(registryCode: string, entryId: number): Promise<EntryReferences> {
  return apiFetch<EntryReferences>(
    `/api/v1/registries/${encodeURIComponent(registryCode)}/entries/${String(entryId)}/references`,
  );
}

/** Ключ під префіксом `entries(code)`: скидається разом із деревом і записом після правки чи виведення. */
export function useEntryReferences(registryCode: string, entryId: number) {
  return useQuery({
    queryKey: [...queryKeys.registries.entries(registryCode), 'references', entryId] as const,
    queryFn: () => getEntryReferences(registryCode, entryId),
    refetchOnWindowFocus: false,
  });
}
