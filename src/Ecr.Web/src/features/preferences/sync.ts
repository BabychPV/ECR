import { deletePreference, putPreference, type UserPreference } from './api';

/**
 * Одне налаштування, що синхронізується з сервером (`BE-20`).
 *
 * ⛔ Ширин колонок сітки тут немає і не буде: вони залежать від екрана
 * конкретного браузера. `sessionStorage` (`lostEdits`, симуляція) — теж ні.
 */
export interface PreferenceBinding {
  /** Ключ на сервері. */
  readonly key: string;
  /** Локальний ВИБІР користувача; `undefined` — вибору не було. */
  stored(): unknown;
  /** Застосовує значення сервера; `false` — значення непридатне. */
  apply(value: unknown): boolean;
}

/** Запис на сервер — підміняється в тестах. */
export interface PreferenceTransport {
  put(key: string, value: unknown): Promise<unknown>;
  remove(key: string): Promise<unknown>;
}

const HttpTransport: PreferenceTransport = { put: putPreference, remove: deletePreference };

/**
 * Узгодження локальних налаштувань із сервером.
 *
 * Правила:
 * - значення сервера перемагає локальне (`reconcile`);
 * - локальне без серверного переноситься на сервер один раз (міграція);
 * - вибір людини пишеться одразу (`changed`), відмова його не відкочує;
 * - той самий вибір двічі не пишеться: `known` — останнє, що має сервер.
 */
export class PreferenceSync {
  /** Ключ → JSON значення, яке (наскільки відомо) лежить на сервері. */
  private readonly known = new Map<string, string>();

  /** Ключі, які людина змінила ДО відповіді сервера: її вибір свіжіший. */
  private readonly touched = new Set<string>();

  constructor(
    private readonly bindings: readonly PreferenceBinding[],
    private readonly transport: PreferenceTransport = HttpTransport,
  ) {}

  /**
   * Застосовує відповідь `GET`. Повертає ключі, значення яких застосовано.
   *
   * ⚠ `known` ставиться ДО `apply`: застосування саме сповіщає про «вибір»
   * (`setDensity`/`setLanguage`/тема), і без цього відлуння пішло б на сервер.
   */
  reconcile(server: readonly UserPreference[]): string[] {
    const applied: string[] = [];

    for (const binding of this.bindings) {
      if (this.touched.has(binding.key)) continue;

      const entry = server.find((item) => item.key === binding.key);

      if (entry !== undefined) {
        this.known.set(binding.key, JSON.stringify(entry.value));
        if (binding.apply(entry.value)) {
          applied.push(binding.key);
          continue;
        }

        // Непридатне значення на сервері: локальне його замінить, а без
        // локального — прибираємо, щоб не застосовувати його щоразу.
        this.known.delete(binding.key);
        if (binding.stored() === undefined) {
          this.transport.remove(binding.key).catch((error: unknown) => {
            reportFailure(binding.key, error);
          });
          continue;
        }
      }

      const local = binding.stored();
      if (local !== undefined) this.send(binding.key, local);
    }

    return applied;
  }

  /** Людина обрала значення. */
  changed(key: string, value: unknown): void {
    if (this.known.get(key) === JSON.stringify(value)) return;

    this.touched.add(key);
    this.send(key, value);
  }

  private send(key: string, value: unknown): void {
    const json = JSON.stringify(value);
    if (this.known.get(key) === json) return;

    this.known.set(key, json);

    this.transport.put(key, value).catch((error: unknown) => {
      // ⚠ Вибір не відкочується; лише забуваємо, що сервер його має, щоб
      // наступна зміна спробувала знову.
      if (this.known.get(key) === json) this.known.delete(key);
      reportFailure(key, error);
    });
  }
}

/** Тихий збій: користувача не турбуємо, розробнику — рядок у консолі. */
export function reportFailure(key: string, error: unknown): void {
  if (import.meta.env.DEV) console.warn(`Preference sync failed: ${key}`, error);
}
