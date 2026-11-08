import { useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import type { CurrentUserDto } from '@/api/types';
import { MeQueryKey } from '@/shared/session/useSession';

/**
 * ✎ UAT my-tasks-collapse (людина: «також в май таск додай колапс»): згорнуті блоки шухляди «My tasks».
 *
 * ⚠ `localStorage` — лише зручність одного браузера (як щільність у `shared/theme/preferences.ts`): ключ несе id
 * користувача, щоб на спільному комп'ютері згортання одного не ховало блоки іншому. Будь-яке звернення до сховища
 * може кинути (приватне вікно, заборонені дані сайту) — тоді блок просто розгорнутий і згортається до перезавантаження.
 */
const Prefix = 'ecr.myTasks.collapsed';

/** Ключ сховища для блоку `section` користувача `userId` (`null` — профіль ще не прочитано). */
export function collapsedStorageKey(userId: number | null, section: string): string {
  return `${Prefix}.${userId === null ? 'anon' : String(userId)}.${section}`;
}

function readCollapsed(key: string): boolean {
  try {
    return globalThis.localStorage?.getItem(key) === '1';
  } catch {
    return false;
  }
}

function writeCollapsed(key: string, collapsed: boolean): void {
  try {
    if (collapsed) globalThis.localStorage?.setItem(key, '1');
    else globalThis.localStorage?.removeItem(key);
  } catch {
    // Сховище недоступне — стан живе лише в пам'яті компонента.
  }
}

/**
 * Стан «згорнуто» блоку шухляди для поточного користувача.
 *
 * ⚠ Користувач — з кешу `/me` (його завжди вже прочитала оболонка), а не новим запитом: шухляда не має смикати
 * профіль заради згортання.
 */
export function useSectionCollapsed(section: string): readonly [boolean, () => void] {
  const client = useQueryClient();
  const userId = client.getQueryData<CurrentUserDto>(MeQueryKey)?.userId ?? null;
  const key = collapsedStorageKey(userId, section);
  const [state, setState] = useState(() => ({ key, collapsed: readCollapsed(key) }));
  const collapsed = state.key === key ? state.collapsed : readCollapsed(key);

  const toggle = (): void => {
    const next = !collapsed;
    writeCollapsed(key, next);
    setState({ key, collapsed: next });
  };

  return [collapsed, toggle] as const;
}
