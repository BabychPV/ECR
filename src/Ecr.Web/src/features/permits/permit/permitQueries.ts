import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { UnitRef } from '@/api/types';
import { getPermit } from '../api/permitsApi';
import { getPermitType } from '../api/typesApi';
import type { Permit, PermitType } from '../api/types';
import { unitLabels } from './names';

/*
 * Запити шапки дозволу — окремо від `usePermitData.ts` (D-132, бюджет маршруту `PermitPage`): сторінка й Overview
 * беруть лише дозвіл, тип і одиниці, а рядки таблиць і пікери (`useQueries`, модель рядка, розбір вставки) потрібні
 * лише вкладці таблиці, що їде окремим чанком. `usePermitData.ts` реекспортує все звідси — імпорти не змінюються.
 */

/*
 * Ключі кешу. Усі під `['permits', …]`: після будь-якої дії над дозволом скидається весь піддомен екземплярів
 * (`permitScope`), а типи (`['permits','types',…]`) цей екран не чіпає — редактор типу має власний кеш.
 */
export const permitKeys = {
  item: (id: number) => ['permits', 'item', id] as const,
  type: (code: string) => ['permits', 'permit-type', code] as const,
  rows: (registryCode: string, permitId: number) => ['permits', 'rows', registryCode, permitId] as const,
  list: (code: string, scope: string) => ['permits', 'pick', code, scope] as const,
  scope: ['permits'] as const,
};

/** Дозвіл: шапка, стан, таблиці з підсумками. */
export function usePermit(id: number) {
  return useQuery<Permit>({
    queryKey: permitKeys.item(id),
    queryFn: () => getPermit(id),
    enabled: Number.isInteger(id) && id > 0,
  });
}

/**
 * Визначення типу (стовпці таблиць).
 *
 * ⚠ `GET /permit-types/{code}` віддає ЧЕРНЕТКУ, якщо вона є, а рядки дозволу живуть у реєстрах ОПУБЛІКОВАНОЇ
 * версії. Поки в типі є неопубліковані зміни, стовпці тут можуть розходитись із реєстром таблиці; сервер на запис
 * відповість помилкою поля, а не мовчки втратить значення.
 */
export function usePermitTypeDefinition(code: string | undefined) {
  return useQuery<PermitType>({
    queryKey: permitKeys.type(code ?? ''),
    queryFn: () => getPermitType(code ?? ''),
    enabled: code !== undefined && code !== '',
  });
}

/** Одиниці виміру (кеш `['units']` спільний із редакторами). */
export function useUnitLabels(enabled: boolean): ReadonlyMap<number, string> {
  const query = useQuery({
    queryKey: ['units'],
    queryFn: () => apiFetch<UnitRef[]>('/api/v1/units'),
    enabled,
  });
  return useMemo(() => unitLabels(query.data ?? []), [query.data]);
}
