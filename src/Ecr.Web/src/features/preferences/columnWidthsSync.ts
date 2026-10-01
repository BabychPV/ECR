import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { DefaultColumnWidth } from '@/features/grid/columnWidths';
import { deletePreference, getPreferences, putPreference, type UserPreference } from './api';
import { reportFailure } from './sync';
import { PreferencesQueryKey } from './usePreferenceSync';

/**
 * Ширини колонок — на КОРИСТУВАЧА і ВИЗНАЧЕННЯ таблиці (`ФВ-14.29`, `D-201`).
 *
 * - Сервер (`BE-20`): ключ `grid.columnWidths.{tableDefId}`, значення
 *   `{ [columnCode]: px }` — лише відхилення від типової ширини.
 * - `localStorage` — кеш першого рендера (без блимання типових ширин), не
 *   джерело правди: значення сервера, щойно воно є в кеші запиту
 *   `['me','preferences']`, перемагає.
 *
 * ⛔ Ключ — `tableDefId`, а не `tableInstanceId`: екземпляр таблиці новий на
 * кожен документ × період, тож ширини губилися б щомісяця, а на сервері такі
 * ключі за рік вичерпали б ліміт 200 налаштувань користувача.
 *
 * ⚠ Компроміс названо в `D-201`: ширини однакові на всіх робочих місцях
 * користувача.
 */

/** Затримка запису на сервер: перетягування межі дає серію подій. */
export const ColumnWidthsDebounceMs = 500;

/** Ширини: код колонки → пікселі. */
type ColumnWidthMap = Readonly<Record<string, number>>;

/** Ключ налаштування на сервері. */
export function columnWidthsKey(tableDefId: number): string {
  return `grid.columnWidths.${String(tableDefId)}`;
}

/** Ключ кешу в `localStorage` (новий: старий `ecr.columnWidths:{tableInstanceId}` не читається). */
export function columnWidthsCacheKey(tableDefId: number): string {
  return `ecr.columnWidths.def:${String(tableDefId)}`;
}

/**
 * Лишає лише придатні ширини; сміття (чужий вміст, пошкоджений JSON із
 * сервера) — не ширина. Налаштування вигляду не має права ламати таблицю.
 */
function sanitizeWidths(value: unknown): Record<string, number> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return {};

  const widths: Record<string, number> = {};
  for (const [code, width] of Object.entries(value as Record<string, unknown>)) {
    if (typeof width === 'number' && Number.isFinite(width) && width > 0) widths[code] = width;
  }

  return widths;
}

/** Лише відхилення від типової ширини: типова не зберігається ніде. */
function widthDeviations(
  widths: ColumnWidthMap,
  defaultWidth: number = DefaultColumnWidth,
): Record<string, number> {
  const result: Record<string, number> = {};
  for (const [code, width] of Object.entries(sanitizeWidths(widths))) {
    if (width !== defaultWidth) result[code] = width;
  }

  return result;
}

/**
 * Серверні ширини таблиці з відповіді `GET /me/preferences`.
 *
 * @returns `undefined` — ключа на сервері немає.
 */
export function serverColumnWidths(
  preferences: readonly UserPreference[],
  tableDefId: number,
): Record<string, number> | undefined {
  if (!Array.isArray(preferences)) return undefined;

  const key = columnWidthsKey(tableDefId);
  const entry = preferences.find((item) => item.key === key);

  return entry === undefined ? undefined : sanitizeWidths(entry.value);
}

/** Кеш першого рендера. Ніколи не падає. */
function readCachedWidths(tableDefId: number): Record<string, number> {
  try {
    const raw = globalThis.localStorage?.getItem(columnWidthsCacheKey(tableDefId));
    if (raw === null || raw === undefined) return {};

    return sanitizeWidths(JSON.parse(raw) as unknown);
  } catch {
    return {};
  }
}

/** Записує кеш; порожні ширини прибирають ключ. */
function writeCachedWidths(tableDefId: number, widths: ColumnWidthMap): void {
  try {
    const key = columnWidthsCacheKey(tableDefId);
    if (Object.keys(widths).length === 0) globalThis.localStorage?.removeItem(key);
    else globalThis.localStorage?.setItem(key, JSON.stringify(widths));
  } catch {
    // Приватне вікно чи заблоковані дані сайту: лишиться лише сервер.
  }
}

/** Залежності запису — підміняються в тестах. */
interface ColumnWidthsWriterDeps {
  /**
   * Останнє відоме серверне значення: `undefined` — ключа немає,
   * `null` — стан сервера невідомий (запит не прийшов або впав).
   */
  readServer(): Record<string, number> | undefined | null;
  /** Оновлює відоме серверне значення; `undefined` — ключ прибрано. */
  writeServer(value: Record<string, number> | undefined): void;
  /** `PUT`; за замовчуванням — `api.ts`. */
  put?(key: string, value: unknown): Promise<unknown>;
  /** `DELETE`; за замовчуванням — `api.ts`. */
  remove?(key: string): Promise<unknown>;
}

/**
 * Відкладений запис ширин однієї таблиці на сервер.
 *
 * Правила:
 * - серія змін за `delayMs` дає один запит;
 * - перед `PUT` зміни зливаються з останнім відомим серверним значенням —
 *   колонки, яких ця сесія не чіпала, не затираються;
 * - усі ширини типові → `DELETE` ключа (якщо відомо, що ключа немає, — нічого);
 * - значення, яке сервер уже має, не пишеться вдруге;
 * - відмова — тихо і без повтору: наступна зміна людини спробує знову.
 */
export class ColumnWidthsWriter {
  private changes: Record<string, number> = {};

  private timer: ReturnType<typeof setTimeout> | undefined;

  constructor(
    private readonly tableDefId: number,
    private readonly deps: ColumnWidthsWriterDeps,
    private readonly defaultWidth: number = DefaultColumnWidth,
    private readonly delayMs: number = ColumnWidthsDebounceMs,
  ) {}

  /** Чи є зміни, ще не відправлені на сервер. */
  get pending(): boolean {
    return this.timer !== undefined;
  }

  /** Людина змінила ширини. */
  change(changed: ColumnWidthMap): void {
    const clean = sanitizeWidths(changed);
    if (Object.keys(clean).length === 0) return;

    this.changes = { ...this.changes, ...clean };
    if (this.timer !== undefined) clearTimeout(this.timer);
    this.timer = setTimeout(() => {
      this.flush();
    }, this.delayMs);
  }

  /** Відкидає ще не відправлені зміни (скидання ширин: інакше вони воскресли б). */
  discard(): void {
    if (this.timer !== undefined) clearTimeout(this.timer);
    this.timer = undefined;
    this.changes = {};
  }

  /** Відправляє накопичене негайно (розмонтування, зміна таблиці). */
  flush(): void {
    if (this.timer !== undefined) clearTimeout(this.timer);
    this.timer = undefined;

    const changes = this.changes;
    this.changes = {};
    if (Object.keys(changes).length === 0) return;

    const key = columnWidthsKey(this.tableDefId);
    const server = this.deps.readServer();
    const next = widthDeviations({ ...(server ?? {}), ...changes }, this.defaultWidth);

    if (server !== null && JSON.stringify(next) === JSON.stringify(server ?? {})) return;

    if (Object.keys(next).length === 0) {
      this.deps.writeServer(undefined);
      const remove = this.deps.remove ?? deletePreference;
      void remove(key).catch((error: unknown) => {
        reportFailure(key, error);
      });
      return;
    }

    this.deps.writeServer(next);
    const put = this.deps.put ?? putPreference;
    void put(key, next).catch((error: unknown) => {
      reportFailure(key, error);
    });
  }
}

/** Що хук віддає сітці. */
interface ColumnWidths {
  /** Відхилення від типової ширини; решта колонок — `DefaultColumnWidth`. */
  readonly widths: ColumnWidthMap;
  /** Зміна ширин (результат `widthsFromEvent`). */
  readonly onResize: (changed: ColumnWidthMap) => void;
  /** «Скинути ширину» (D-234): прибирає ширини користувача — лишаються шаблонні/типові. */
  readonly reset: () => void;
}

interface LocalState {
  readonly tableDefId: number;
  readonly widths: Record<string, number>;
  /** Людина змінювала ширини цієї таблиці: її вибір свіжіший за сервер. */
  readonly touched: boolean;
}

function upsertPreference(
  current: UserPreference[] | undefined,
  key: string,
  value: Record<string, number> | undefined,
): UserPreference[] | undefined {
  // ⚠ Кешу ще немає — не вигадуємо його: відповідь `GET` прийде сама.
  if (current === undefined || !Array.isArray(current)) return current;

  const rest = current.filter((item) => item.key !== key);
  if (value === undefined) return rest;

  return [...rest, { key, value, updatedAt: new Date().toISOString() } as UserPreference];
}

/**
 * Ширини колонок таблиці `tableDefId` для поточного користувача.
 *
 * ⚠ Читає той самий запит `['me','preferences']`, що й оболонка
 * (`usePreferenceSync`), з тими самими опціями: `GET` — один за сеанс.
 * Після запису кеш запиту оновлюється, тож інший період тієї самої таблиці
 * відкривається вже з новими ширинами.
 */
export function useColumnWidths(
  tableDefId: number,
  defaultWidth: number = DefaultColumnWidth,
): ColumnWidths {
  const queryClient = useQueryClient();
  const query = useQuery({
    queryKey: PreferencesQueryKey,
    queryFn: getPreferences,
    retry: false,
    staleTime: Infinity,
    refetchOnWindowFocus: false,
  });

  const [state, setState] = useState<LocalState>(() => ({
    tableDefId,
    widths: readCachedWidths(tableDefId),
    touched: false,
  }));

  // Інша таблиця — інший стан (похідний стан під час рендера, без ефекту).
  let local = state;
  if (state.tableDefId !== tableDefId) {
    local = { tableDefId, widths: readCachedWidths(tableDefId), touched: false };
    setState(local);
  }

  const server = useMemo(
    () => (query.data === undefined ? undefined : serverColumnWidths(query.data, tableDefId)),
    [query.data, tableDefId],
  );

  // Сервер перемагає кеш — доки людина сама не змінила ширини.
  const widths = !local.touched && server !== undefined ? server : local.widths;

  // Значення сервера стає кешем наступного першого рендера.
  useEffect(() => {
    if (!local.touched && server !== undefined) writeCachedWidths(tableDefId, server);
  }, [local.touched, server, tableDefId]);

  const writer = useRef<ColumnWidthsWriter | null>(null);
  useEffect(() => {
    const key = columnWidthsKey(tableDefId);
    const current = new ColumnWidthsWriter(
      tableDefId,
      {
        readServer: () => {
          const data = queryClient.getQueryData<UserPreference[]>(PreferencesQueryKey);
          return data === undefined ? null : serverColumnWidths(data, tableDefId);
        },
        writeServer: (value) => {
          queryClient.setQueryData<UserPreference[] | undefined>(PreferencesQueryKey, (old) =>
            upsertPreference(old, key, value),
          );
        },
      },
      defaultWidth,
    );
    writer.current = current;

    return () => {
      // Розмонтування чи інша таблиця: накопичене не губиться.
      current.flush();
      if (writer.current === current) writer.current = null;
    };
  }, [tableDefId, defaultWidth, queryClient]);

  const onResize = useCallback(
    (changed: ColumnWidthMap) => {
      const clean = sanitizeWidths(changed);
      if (Object.keys(clean).length === 0) return;

      const next = widthDeviations({ ...widths, ...clean }, defaultWidth);
      writeCachedWidths(tableDefId, next);
      setState({ tableDefId, widths: next, touched: true });
      writer.current?.change(clean);
    },
    [widths, defaultWidth, tableDefId],
  );

  const reset = useCallback(() => {
    const key = columnWidthsKey(tableDefId);
    writer.current?.discard();
    writeCachedWidths(tableDefId, {});
    setState({ tableDefId, widths: {}, touched: true });

    // Ключа на сервері вже немає — нічого видаляти (і кеш не вигадуємо).
    const data = queryClient.getQueryData<UserPreference[]>(PreferencesQueryKey);
    if (data !== undefined && serverColumnWidths(data, tableDefId) === undefined) return;

    queryClient.setQueryData<UserPreference[] | undefined>(PreferencesQueryKey, (old) =>
      upsertPreference(old, key, undefined),
    );
    void deletePreference(key).catch((error: unknown) => {
      reportFailure(key, error);
    });
  }, [tableDefId, queryClient]);

  return { widths, onResize, reset };
}
