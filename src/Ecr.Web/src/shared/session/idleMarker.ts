import { IDLE_SIGNOUT_MARKER_KEY } from '@/api/client';

/**
 * ✎ Борг 09.11: читання й зняття позначки «вкладку виведено після бездіяльності» (`rememberIdleSignOut` у
 * `api/client.ts`). Окремим модулем — лише для сторінки входу: спільний чанк оболонки на межі бюджету (D-132).
 */

/** Скільки позначка дійсна: довше — це вже не «щойно вивели», а давній вихід. */
const IdleSignOutMarkerTtlMs = 12 * 60 * 60 * 1000;

/** Позначка виходу після бездіяльності, якщо вона свіжа; інакше `null`. */
export function readIdleSignOut(): { minutes: number | null } | null {
  try {
    const raw = sessionStorage.getItem(IDLE_SIGNOUT_MARKER_KEY);
    if (raw === null) return null;
    const parsed = JSON.parse(raw) as { minutes?: unknown; at?: unknown };
    if (typeof parsed.at !== 'number' || Date.now() - parsed.at > IdleSignOutMarkerTtlMs) return null;
    return { minutes: typeof parsed.minutes === 'number' ? parsed.minutes : null };
  } catch {
    return null;
  }
}

/** Знімає позначку (після входу). */
export function forgetIdleSignOut(): void {
  try {
    sessionStorage.removeItem(IDLE_SIGNOUT_MARKER_KEY);
  } catch {
    // Нічого прибирати.
  }
}
