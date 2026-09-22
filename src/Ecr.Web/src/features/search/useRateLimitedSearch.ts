import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { UseQueryResult } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import { useDataSearch, type SearchHit } from './api';

/**
 * Пошук палітри з поштивою реакцією на межу частоти сервера (BE-19).
 *
 * Сервер пускає 30 запитів за 10 с на користувача, понад це — `429` із кодом
 * `ECR-REQ-0429` і `Retry-After`. Це не відмова в сенсі «щось зламано»: межа
 * минає сама, тож палітра чекає вказаний строк і повторює ПОТОЧНИЙ текст поля.
 *
 * ⛔ Під час очікування запитів немає зовсім: ключ запиту заморожено на тексті,
 * що дав `429`, і набір нових символів мережу не чіпає. Після очікування йде
 * рівно один запит — поточним текстом.
 *
 * ⛔ Повтор не безкінечний: `SearchRateLimitMaxInARow` відмов `429` поспіль
 * (автоматичний ланцюг) — і далі звичайна відмова через `ErrorAlert`.
 */

/** Код відмови межі частоти — той самий, що `ErrorCodes.TooManyRequests` на сервері. */
export const SearchRateLimitCode = 'ECR-REQ-0429';

/** Строк очікування, якщо сервер не надіслав (або надіслав нерозбірний) `Retry-After`. */
export const SearchRetryDefaultSeconds = 2;

/** Скільки `429` поспіль у ланцюгу автоматичних повторів — і далі звичайна відмова. */
export const SearchRateLimitMaxInARow = 3;

/** Строк очікування, якщо відмова — саме межа частоти пошуку; інакше `null`. */
export function rateLimitWaitSeconds(error: unknown): number | null {
  if (!(error instanceof EcrApiError) || error.problem.errorCode !== SearchRateLimitCode) return null;

  return error.problem.retryAfterSeconds ?? SearchRetryDefaultSeconds;
}

export interface RateLimitedSearch {
  readonly search: UseQueryResult<SearchHit[]>;
  /** Секунди до повтору, поки триває очікування; інакше `null`. */
  readonly waitSeconds: number | null;
  /** Відмова, яку показувати звичайним `ErrorAlert` (будь-яка, крім очікуваної межі). */
  readonly failed: boolean;
  /** Повтор із `ErrorAlert`: дія користувача починає новий ланцюг. */
  readonly retry: () => void;
}

interface Hold {
  readonly term: string;
  readonly seconds: number;
}

/** Текст, відпущений після очікування, — дійсний, доки не зміниться `debounced`. */
interface Released {
  readonly term: string;
  readonly from: string;
}

/**
 * @param opened  чи відкрита палітра: закрита — ні запитів, ні таймера;
 * @param text    поточний текст поля (саме його повторюємо після очікування);
 * @param debounced текст після затримки введення.
 */
export function useRateLimitedSearch(opened: boolean, text: string, debounced: string): RateLimitedSearch {
  const [hold, setHold] = useState<Hold | null>(null);
  const [released, setReleased] = useState<Released | null>(null);
  const [exhaustedAt, setExhaustedAt] = useState<string | null>(null);

  const settled = released !== null && released.from === debounced ? released.term : debounced;
  const term = !opened ? '' : hold !== null ? hold.term : settled.trim();

  const search = useDataSearch(term);

  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const strikes = useRef(0);
  const retrying = useRef(false);
  const handledAt = useRef('');

  // Останні значення для колбека таймера: він живе довше за рендер, що його поставив.
  const latest = useRef({ text, debounced, refetch: search.refetch });
  latest.current = { text, debounced, refetch: search.refetch };

  const cancel = useCallback((): void => {
    if (timer.current !== null) clearTimeout(timer.current);
    timer.current = null;
  }, []);

  // Закрито — таймер скасовано, стан скинуто; після закриття запитів немає.
  useEffect(() => {
    if (opened) return;

    cancel();
    strikes.current = 0;
    retrying.current = false;
    setHold(null);
    setReleased(null);
    setExhaustedAt(null);
  }, [opened, cancel]);

  useEffect(() => cancel, [cancel]);

  const limitedSeconds = search.isError ? rateLimitWaitSeconds(search.error) : null;
  // Одна конкретна відмова: той самий час у різних ключів — різні відмови.
  const errorKey = `${term}@${String(search.errorUpdatedAt)}`;

  // ⚠ Layout-ефект: рішення «чекати чи показати відмову» має впасти ДО
  // малювання, інакше третя `429` блимнула б рядком очікування.
  useLayoutEffect(() => {
    if (!opened) return;

    if (limitedSeconds === null) {
      // Успіх або інша відмова обривають ланцюг.
      if (search.isSuccess || search.isError) {
        strikes.current = 0;
        retrying.current = false;
      }
      return;
    }

    if (handledAt.current === errorKey) return;
    handledAt.current = errorKey;

    strikes.current = retrying.current ? strikes.current + 1 : 1;
    retrying.current = false;

    if (strikes.current >= SearchRateLimitMaxInARow) {
      setExhaustedAt(errorKey);
      return;
    }

    const frozen = term;
    setHold({ term: frozen, seconds: limitedSeconds });

    // Одне очікування одночасно.
    cancel();
    timer.current = setTimeout(() => {
      timer.current = null;
      const current = latest.current.text.trim();

      retrying.current = true;
      setHold(null);
      setReleased({ term: current, from: latest.current.debounced });

      // Той самий текст — ключ не зміниться, тож повтор явний. Інший текст —
      // новий ключ сам дасть рівно один запит.
      if (current === frozen) void latest.current.refetch();
    }, limitedSeconds * 1000);
  }, [opened, limitedSeconds, search.isSuccess, search.isError, errorKey, term, cancel]);

  const exhausted = exhaustedAt !== null && exhaustedAt === errorKey;

  const retry = useCallback((): void => {
    strikes.current = 0;
    retrying.current = false;
    setExhaustedAt(null);
    void latest.current.refetch();
  }, []);

  return {
    search,
    // ⚠ Без `hold`, але з відмовою `429` — це або кадр до layout-ефекту, або
    // повтор уже в мережі (тоді показується завантаження, а не «повторю за»).
    waitSeconds:
      hold !== null
        ? hold.seconds
        : limitedSeconds !== null && !exhausted && !search.isFetching
          ? limitedSeconds
          : null,
    failed: search.isError && (limitedSeconds === null || exhausted),
    retry,
  };
}
