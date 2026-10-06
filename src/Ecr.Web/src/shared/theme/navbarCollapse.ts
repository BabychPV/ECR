import { useSyncExternalStore } from 'react';

/**
 * Згорнуте бічне меню («лише іконки») — налаштування вигляду користувача.
 *
 * ⚠ Та сама модель, що й щільність (`preferences.ts`): `localStorage` — кеш
 * першого рендера (без стрибка ширини після F5), сервер (`BE-20`, ключ
 * `navbarCollapsed`) — джерело правди між пристроями; синхронізує
 * `usePreferenceSync`.
 */
const StorageKey = 'ecr.navbarCollapsed';

/** Ключ налаштування на сервері (`PUT /api/v1/me/preferences/navbarCollapsed`). */
export const NavbarCollapsedPreferenceKey = 'navbarCollapsed';

/**
 * Вибір користувача; `undefined` — не обирав.
 *
 * ⚠ Читання не падає ніколи: у приватному вікні звернення до `localStorage`
 * кидає виняток, а налаштування вигляду не привід не підняти застосунок.
 */
export function storedNavbarCollapsed(): boolean | undefined {
  try {
    const raw = globalThis.localStorage?.getItem(StorageKey);
    if (raw === 'true') return true;
    if (raw === 'false') return false;
    return undefined;
  } catch {
    return undefined;
  }
}

function navbarCollapsed(): boolean {
  return storedNavbarCollapsed() ?? false;
}

const listeners = new Set<() => void>();
const chosenListeners = new Set<(value: boolean) => void>();

/** Підписує на ВИБІР (для сервера); повертає відписку. */
export function onNavbarCollapsedChosen(listener: (value: boolean) => void): () => void {
  chosenListeners.add(listener);

  return () => {
    chosenListeners.delete(listener);
  };
}

/** Запам'ятовує стан меню і перемальовує підписників. */
export function setNavbarCollapsed(value: boolean): void {
  try {
    globalThis.localStorage?.setItem(StorageKey, String(value));
  } catch {
    // Налаштування вигляду — не привід ламати роботу.
  }

  // ⚠ Копії наборів: підписник має право відписатися прямо з обробника.
  for (const notify of [...listeners]) notify();
  for (const notify of [...chosenListeners]) notify(value);
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);

  return () => {
    listeners.delete(listener);
  };
}

/** Чи згорнуте меню — підписка, що сама перемальовує компонент. */
export function useNavbarCollapsed(): boolean {
  return useSyncExternalStore(subscribe, navbarCollapsed, navbarCollapsed);
}
