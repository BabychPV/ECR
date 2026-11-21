import { keepPreviousData, useQuery, type UseQueryResult } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { EcrApiError } from '@/api/client';
import { validateMethodologyScript } from './api';
import { scriptKeys } from './scriptQueryKeys';
import type { ScriptValidationDto } from './types';

/**
 * Жива перевірка тексту скрипта (`POST …/script/validate`) зі спокійною реакцією на межу частоти сервера (PSS-21).
 *
 * Сервер пускає 120 перевірок за хвилину на користувача, понад це — `429 ECR-REQ-0429` з `Retry-After`. Це не
 * помилка тексту і не збій: межа минає сама. Тож перевірка СТАЄ НА ПАУЗУ на вказаний строк (рядок стану редактора
 * каже «перевірки на паузі, повтор через N с»), а потім сама перевіряє ПОТОЧНИЙ текст.
 *
 * ⛔ Під час паузи запитів немає зовсім (`enabled: false`): набір нових символів мережу не чіпає, інакше кожна пауза
 * введення давала б ще одну `429` і пауза не закінчувалася б. Після паузи — рівно один запит поточним текстом.
 *
 * ⚠ Строк показується одним числом, без щосекундного відліку: рядок стану — `aria-live`, і відлік читався б
 * екранним диктором щосекунди.
 */

/** Код відмови межі частоти — той самий, що `ErrorCodes.TooManyRequests` на сервері. */
export const ScriptCheckRateLimitCode = 'ECR-REQ-0429';

/** Пауза, якщо сервер не надіслав (або надіслав нерозбірний) `Retry-After`. */
export const ScriptCheckRetryDefaultSeconds = 5;

/** Скільки секунд чекати, якщо відмова — саме межа частоти; інакше `null`. */
export function scriptCheckPauseSeconds(error: unknown): number | null {
  if (!(error instanceof EcrApiError) || error.problem.errorCode !== ScriptCheckRateLimitCode) return null;

  const advised = error.problem.retryAfterSeconds;
  return advised !== undefined && advised > 0 ? advised : ScriptCheckRetryDefaultSeconds;
}

export interface ScriptValidation {
  readonly validation: UseQueryResult<ScriptValidationDto>;
  /** Секунди паузи після `429`, поки вона триває; інакше `null`. */
  readonly pausedSeconds: number | null;
}

/**
 * @param methodologyId методологія;
 * @param versionId версія;
 * @param text текст після затримки введення — саме його перевіряємо;
 * @param enabled чи перевіряти взагалі (текст завантажено, версія — скрипт, текст не порожній).
 */
export function useScriptValidation(
  methodologyId: number,
  versionId: number,
  text: string,
  enabled: boolean,
): ScriptValidation {
  const [pause, setPause] = useState<number | null>(null);

  const validation = useQuery({
    queryKey: scriptKeys.validation(versionId, text),
    queryFn: ({ signal }) => validateMethodologyScript(methodologyId, versionId, { text }, signal),
    enabled: enabled && pause === null,
    placeholderData: keepPreviousData,
    gcTime: 0,
    staleTime: Infinity,
    retry: false,
  });

  const seconds = validation.isError ? scriptCheckPauseSeconds(validation.error) : null;
  const handledAt = useRef(0);

  // Одна конкретна відмова — одна пауза (той самий `errorUpdatedAt` після паузи не ставить нової).
  useEffect(() => {
    if (seconds === null || handledAt.current === validation.errorUpdatedAt) return;

    handledAt.current = validation.errorUpdatedAt;
    setPause(seconds);
  }, [seconds, validation.errorUpdatedAt]);

  // Після паузи запит знову дозволено: той самий ключ без даних react-query перепитує сам, новий — тим паче.
  useEffect(() => {
    if (pause === null) return undefined;

    const timer = setTimeout(() => {
      setPause(null);
    }, pause * 1000);
    return () => {
      clearTimeout(timer);
    };
  }, [pause]);

  return { validation, pausedSeconds: pause };
}
