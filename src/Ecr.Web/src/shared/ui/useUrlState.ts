import { useCallback, useLayoutEffect, useRef } from 'react';
import { useLocation, useNavigate, type Location } from 'react-router-dom';

type Updates = Record<string, string | number | null>;

/**
 * Запит, який сеттери цього файлу вже ВІДПРАВИЛИ, але дерево ще не відрендерило.
 *
 * ⛔ Не `setSearchParams((current) => …)`. У react-router 7.18 `current` — це
 * `new URLSearchParams(searchParams)`, де `searchParams` — `useMemo` над
 * `location.search` ОСТАННЬОГО РЕНДЕРУ хука (`react-router/dist/development/
 * chunk-BV7QT456.mjs`, `useSearchParams`: `nextInit(new URLSearchParams(searchParams))`,
 * залежності `[navigate, searchParams]`). Сеттер, викликаний до перерендеру
 * (слухач, зареєстрований один раз; дерево, яке `RouterProvider` оновлює у
 * `startTransition`; другий сеттер у тому самому обробнику), перебудовував
 * запит зі старого знімка й мовчки відкочував щойно змінений параметр.
 *
 * ⚠ Ключ — САМ об'єкт локації, від якої рахувався запит (react-router тримає
 * його ідентичність до наступного переходу). Поки дерево показує ту саму
 * локацію, наступний сеттер будує від цього запиту; щойно перехід
 * закомічено — від нової локації. Інший роутер (інша вкладка тесту) має інший
 * об'єкт локації, тож запит із нього сюди не протікає.
 */
let pending: { from: Location; search: string } | null = null;

/** Спільне ядро всіх сеттерів: застосувати `updates` до ЖИВОЇ адреси одним переходом. */
function useApplyUrlUpdates(): (updates: Updates) => void {
  const location = useLocation();
  const navigate = useNavigate();

  // ⚠ Ref, а не замикання: сеттер мусить бачити останню закомічену локацію,
  // навіть якщо його самого захопили кілька рендерів тому.
  const live = useRef({ location, navigate });
  useLayoutEffect(() => {
    live.current = { location, navigate };
  }, [location, navigate]);

  return useCallback((updates: Updates) => {
    const { location: current, navigate: go } = live.current;
    const base = pending !== null && pending.from === current ? pending.search : current.search;
    const next = new URLSearchParams(base);

    for (const [name, value] of Object.entries(updates)) {
      // ⚠ Порожнє значення ПРИБИРАЄ параметр, а не пише `?code=`: адреса
      // з порожніми хвостами накопичує їх і стає нечитабельною за три
      // зміни фільтра.
      if (value === null || value === '') {
        next.delete(name);
      } else {
        next.set(name, String(value));
      }
    }

    const search = `?${next.toString()}`;
    pending = { from: current, search };
    go(search, { replace: true });
  }, []);
}

/**
 * Вибір користувача живе **в адресі сторінки** (`ФВ-14.29`).
 *
 * ⛔ Не в `useState`. Обраний період, довідник, проєкт, вкладка — це те, що
 * людина хоче надіслати колезі («подивись ось це») і те, до чого вона
 * повертається завтра. У локальному стані все це зникає при перезавантаженні,
 * а посилання веде на порожній екран із проханням обрати щось наново.
 *
 * ⚠ `replace: true` навмисно: зміна фільтра НЕ додає запис в історію.
 * Інакше «Назад» після десяти уточнень фільтра десять разів вертало б до
 * попереднього значення замість того, щоб піти зі сторінки.
 */
export function useUrlState(
  name: string,
): [string | null, (value: string | null) => void] {
  const location = useLocation();
  const apply = useApplyUrlUpdates();

  const set = useCallback(
    (value: string | null) => {
      apply({ [name]: value });
    },
    [name, apply],
  );

  const params = new URLSearchParams(location.search);
  return [params.get(name), set];
}

/** Те саме для числового параметра. */
export function useUrlNumber(name: string): [number | null, (value: number | null) => void] {
  const [raw, setRaw] = useUrlState(name);

  const set = useCallback(
    (value: number | null) => {
      setRaw(value === null ? null : String(value));
    },
    [setRaw],
  );

  // ⚠ Нечислове значення в адресі трактується як відсутнє, а не як `NaN`:
  // адресу правлять руками, і `?periodKey=abc` не має ламати екран.
  //
  // ⛔ Аудит 2026-09-16 §10.8: ПОРОЖНЄ значення (`?periodKey=`, а також
  // `?periodKey=%20`) теж мусить читатися як відсутнє. `Number('')` дає `0` —
  // скінченний і правдоподібний, — і перевірка `Number.isFinite` нижче
  // пропускала його як справжній період. Наслідок тихий: `urlPeriod ??
  // currentPeriodKey()` (`DocumentPage.tsx`) бачив `0`, а не `null`, тож
  // дефолт не спрацьовував і весь екран ходив по періоду 0 — той самий
  // симптом, що `A7-28` («валідація мовчки йшла по періоду 0, відповідаючи
  // "помилок немає"»), лише з боку адреси.
  const trimmed = raw === null ? null : raw.trim();
  const parsed = trimmed === null || trimmed.length === 0 ? Number.NaN : Number(trimmed);

  return [Number.isFinite(parsed) ? parsed : null, set];
}

/**
 * Онова КІЛЬКОХ параметрів адреси ОДНИМ переходом.
 *
 * ⛔ UI-аудит, lane 3: два окремі виклики сеттера `useUrlState` в одному
 * обробнику (наприклад, `setPeriodKey(value)` одразу за ним `setCursor(null)`)
 * колись НЕ компонувалися: другий будував запит зі знімка ДО першого й мовчки
 * відкочував його (див. `pending` вгорі файлу). Наслідок у `DocumentsPage.tsx` —
 * поле «Period» набирало значення на екрані, але жоден запит не бачив
 * `periodKey` в адресі. Тепер окремі сеттери компонуються, але один перехід
 * усе одно чесніший: одна зміна адреси замість кількох `replace` поспіль. Коли
 * треба змінити більш ніж один параметр за одну дію — використовуй цей хук.
 */
export function useUrlParamsSetter(): (updates: Updates) => void {
  return useApplyUrlUpdates();
}
