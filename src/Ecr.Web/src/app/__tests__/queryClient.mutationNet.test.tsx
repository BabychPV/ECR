import type { JSX } from 'react';
import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import { QueryClientProvider, useMutation } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import { createQueryClient } from '@/app/queryClient';

/**
 * `UI-00`: зміна, що не вдалася, не може лишитися непоміченою.
 *
 * ⛔ Чому це не «на всяк випадок». Найдорожчий сценарій системи — оператор
 * натиснув «Подати», сервер відмовив, екран не змінився, і людина впевнена, що
 * звіт подано. Далі про це дізнається інспектор, а не розробник.
 *
 * ⚠ Сітка сьогодні ловить НУЛЬ випадків: усі 80 викликів `useMutation(` у `src`
 * мають власний `onError`. Вона заводиться не для них, а для 81-го — і саме
 * тому тест перевіряє ОБИДВІ гілки: що зміна без обробника кричить і що зміна
 * з обробником не кричить двічі.
 */

/** Відмова сервера в тій самій формі, у якій її бачить застосунок. */
function serverRefusal(): EcrApiError {
  return new EcrApiError({
    type: 'about:blank',
    title: 'Conflict',
    status: 409,
    errorCode: 'ECR-PRD-4223',
    detail: 'Період закрито — спершу відкрийте період.',
    correlationId: 'test-correlation',
  });
}

function Harness({ children }: { readonly children: JSX.Element }): JSX.Element {
  return (
    <MantineProvider>
      <Notifications />
      <QueryClientProvider client={createQueryClient()}>{children}</QueryClientProvider>
    </MantineProvider>
  );
}

/** Зміна БЕЗ власного `onError` — саме те, що сітка має прикрити. */
function Unguarded(): JSX.Element {
  const submit = useMutation({ mutationFn: () => Promise.reject(serverRefusal()) });

  return (
    <button type="button" onClick={() => submit.mutate()}>
      Подати
    </button>
  );
}

/** Зміна з власним обробником — сітка має мовчати, інакше буде два тости. */
function Guarded(): JSX.Element {
  const submit = useMutation({
    mutationFn: () => Promise.reject(serverRefusal()),
    onError: () => {
      notifications.show({ message: 'Власний обробник: період закрито.' });
    },
  });

  return (
    <button type="button" onClick={() => submit.mutate()}>
      Подати
    </button>
  );
}

beforeEach(() => {
  notifications.cleanQueue();
  notifications.clean();
});

afterEach(() => {
  cleanup();
});

describe('UI-00 — страхувальна сітка під змінами', () => {
  it('зміна без власного onError показує причину відмови так, як її назвав сервер', async () => {
    render(
      <Harness>
        <Unguarded />
      </Harness>,
    );

    await act(async () => {
      screen.getByRole('button', { name: 'Подати' }).click();
    });

    /*
     * ⛔ Перевіряється ТЕКСТ сервера, а не факт появи тоста. «Щось пішло не
     * так» теж було б тостом — і теж не сказало б оператору, що робити.
     */
    await waitFor(() => {
      expect(screen.getByText('Період закрито — спершу відкрийте період.')).toBeDefined();
    });
  });

  it('зміна з власним onError не показує ДВА тости на одну відмову', async () => {
    render(
      <Harness>
        <Guarded />
      </Harness>,
    );

    await act(async () => {
      screen.getByRole('button', { name: 'Подати' }).click();
    });

    await waitFor(() => {
      expect(screen.getByText('Власний обробник: період закрито.')).toBeDefined();
    });

    /*
     * ⚠ Мутаційний доказ саме тут: приберіть перевірку `mutation.options.onError`
     * у `queryClient.ts` — і поруч з'явиться другий тост із текстом сервера,
     * тобто `queryAllByText` дасть 1 замість 0, і цей рядок упаде.
     */
    expect(screen.queryAllByText('Період закрито — спершу відкрийте період.')).toHaveLength(0);
  });
});
