import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import { fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { MappedFieldPreview } from '@/api/types';
import { PauseResumeAction } from '@/features/mapping/PauseResumeAction';

/**
 * Кнопка паузи/відновлення мапінгу в рядку перегляду (`BE-27`).
 *
 * ⛔ Перевіряються АДРЕСА, МЕТОД і ID у виклику — той самий клас доказу, що й
 * `lifecycle.test.ts` для самих функцій `api.ts`: клік не на ту адресу
 * жоден серверний тест не ловить, бо він іде своїм маршрутом сам.
 */

const activeField: MappedFieldPreview = {
  fieldMapId: 41,
  sourceField: 'Flare_01_CO',
  outcome: 'Materialized',
  targetRowKey: 'Flare_01',
  targetColumnDefId: 100,
  targetColumnCode: 'CO_MASS',
  aggregation: 'Sum',
  sourceUnitCode: 'kg',
  targetUnitCode: 't',
  pointCount: 2,
  foldedValue: '42.5',
  isActive: true,
  pendingSourceUnitChange: null,
};

const pausedField: MappedFieldPreview = {
  ...activeField,
  fieldMapId: 42,
  isActive: false,
};

/** Записані виклики `fetch`, у порядку надсилання. */
const sent: { url: string; method: string }[] = [];

function mockServer(status = 200): void {
  sent.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      sent.push({ url: String(url), method: String(init?.method ?? 'GET') });

      if (status !== 200) {
        return new Response(
          JSON.stringify({
            title: 'conflict',
            status,
            errorCode: 'ECR-INT-0409',
            correlationId: 'cid-1',
          }),
          { status, headers: { 'Content-Type': 'application/json' } },
        );
      }

      return new Response(JSON.stringify({ id: 41, isActive: false }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

function show(field: MappedFieldPreview): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <PauseResumeAction field={field} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  cleanup();
});

describe('PauseResumeAction: пауза й відновлення з рядка перегляду', () => {
  it('клік «Pause» на активному мапінгу викликає pauseEntityFieldMap із правильним id', async () => {
    mockServer();
    show(activeField);

    fireEvent.click(screen.getByText('⟦mapping.pause⟧'));

    await waitFor(() =>
      expect(sent).toEqual([{ url: '/api/v1/entity-field-maps/41/pause', method: 'POST' }]),
    );
  });

  it('клік «Resume» на призупиненому мапінгу викликає resumeEntityFieldMap із правильним id', async () => {
    mockServer();
    show(pausedField);

    fireEvent.click(screen.getByText('⟦mapping.resume⟧'));

    await waitFor(() =>
      expect(sent).toEqual([{ url: '/api/v1/entity-field-maps/42/resume', method: 'POST' }]),
    );
  });

  // ⛔ Мутаційний доказ: якби кнопка малювалась БЕЗ урахування `isActive`
  // (наприклад, обидві завжди), цей тест — єдиний, хто про це скаже.
  it('на активному мапінгу немає кнопки «Resume», а на призупиненому — «Pause»', () => {
    mockServer();
    show(activeField);
    expect(screen.queryByText('⟦mapping.resume⟧')).toBeNull();
    expect(screen.getByText('⟦mapping.pause⟧')).toBeDefined();
    cleanup();

    show(pausedField);
    expect(screen.queryByText('⟦mapping.pause⟧')).toBeNull();
    expect(screen.getByText('⟦mapping.resume⟧')).toBeDefined();
  });

  it('відмова сервера показує ErrorAlert і не забирає кнопку', async () => {
    mockServer(409);
    show(activeField);

    fireEvent.click(screen.getByText('⟦mapping.pause⟧'));

    await waitFor(() => expect(screen.getByRole('alert')).toBeDefined());

    // ⛔ Документ не блокується (`ФВ-14.24`): кнопка лишається доступною для
    // повторної спроби, а не зникає разом з рештою рядка.
    expect(screen.getByText('⟦mapping.pause⟧')).toBeDefined();
  });
});
