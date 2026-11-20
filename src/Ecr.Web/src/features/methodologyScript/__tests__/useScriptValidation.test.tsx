import type { JSX, ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { EcrApiError, type EcrProblem } from '@/api/client';
import {
  ScriptCheckRetryDefaultSeconds,
  scriptCheckPauseSeconds,
  useScriptValidation,
} from '../useScriptValidation';

const validate = vi.hoisted(() => vi.fn());

vi.mock('@/features/methodologyScript/api', () => ({ validateMethodologyScript: validate }));

/**
 * PSS-21 (рецензія, п. 6): `429` живої перевірки скрипта — не тиша, а пауза на `Retry-After` і САМ повтор поточного
 * тексту; під час паузи запитів немає.
 *
 * Мутаційний доказ: `enabled` без `pause === null` — під час паузи новий текст іде в мережу («немає запитів»
 * червоніє); прибрати таймер паузи — повтору немає («сама перевіряє»).
 */
let client: QueryClient;
const wrapper = ({ children }: { children: ReactNode }): JSX.Element => (
  <QueryClientProvider client={client}>{children}</QueryClientProvider>
);

const Ok = { diagnostics: [], outline: null };

function problem(status: number, errorCode: string, retryAfterSeconds?: number): EcrApiError {
  const body: EcrProblem = { title: 'x', status, errorCode, correlationId: 'c' };
  if (retryAfterSeconds !== undefined) body.retryAfterSeconds = retryAfterSeconds;
  return new EcrApiError(body);
}

function tooMany(retryAfterSeconds?: number): EcrApiError {
  return problem(429, 'ECR-REQ-0429', retryAfterSeconds);
}

beforeEach(() => {
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
});

afterEach(() => {
  validate.mockReset();
});

describe('useScriptValidation', () => {
  it('429 — пауза на Retry-After, без запитів під час паузи, потім сама перевіряє поточний текст', async () => {
    validate.mockRejectedValueOnce(tooMany(1)).mockResolvedValue(Ok);

    const { result, rerender } = renderHook(
      ({ text }: { text: string }) => useScriptValidation(1, 2, text, true),
      { wrapper, initialProps: { text: 'var a = 1;' } },
    );

    await waitFor(() => {
      expect(result.current.pausedSeconds).toBe(1);
    });
    expect(validate).toHaveBeenCalledTimes(1);

    // Набір під час паузи мережу не чіпає.
    rerender({ text: 'var a = 2;' });
    await act(async () => {
      await new Promise((done) => setTimeout(done, 300));
    });
    expect(validate).toHaveBeenCalledTimes(1);
    expect(result.current.pausedSeconds).toBe(1);

    // Пауза минула — рівно один запит, ПОТОЧНИМ текстом; пауза знята.
    await waitFor(
      () => {
        expect(result.current.validation.isSuccess).toBe(true);
      },
      { timeout: 3_000 },
    );
    expect(result.current.pausedSeconds).toBeNull();
    expect(validate).toHaveBeenCalledTimes(2);
    expect(validate.mock.calls[1]?.[2]).toEqual({ text: 'var a = 2;' });
  });

  it('той самий текст після паузи перевіряється знову', async () => {
    validate.mockRejectedValueOnce(tooMany(1)).mockResolvedValue(Ok);

    const { result } = renderHook(() => useScriptValidation(1, 2, 'var a = 1;', true), { wrapper });

    await waitFor(() => {
      expect(result.current.pausedSeconds).toBe(1);
    });
    await waitFor(
      () => {
        expect(result.current.validation.isSuccess).toBe(true);
      },
      { timeout: 3_000 },
    );
    expect(validate).toHaveBeenCalledTimes(2);
    expect(result.current.pausedSeconds).toBeNull();
  });

  it('інша відмова — не пауза; без Retry-After — типова пауза', () => {
    expect(scriptCheckPauseSeconds(new Error('network'))).toBeNull();
    expect(scriptCheckPauseSeconds(problem(422, 'ECR-CALC-0422'))).toBeNull();
    expect(scriptCheckPauseSeconds(tooMany())).toBe(ScriptCheckRetryDefaultSeconds);
    expect(scriptCheckPauseSeconds(tooMany(7))).toBe(7);
  });
});
