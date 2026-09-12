import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { JobsPage } from '@/pages/admin/JobsPage';

/**
 * Поле ідентифікатора задачі не реагувало на Enter — лише клік по «Watch».
 * Ідентифікатор найчастіше приходить вставленим із чужого повідомлення
 * («подивись, чому впало»), і природний наступний рух — Enter, не потяг
 * миші до кнопки.
 *
 * ⛔ Мутаційний доказ: перевіряється, що Enter приводить до ТОГО САМОГО
 * спостережуваного ефекту, що й клік «Watch» — ідентифікатор потрапляє в
 * адресу (`?id=...`, `useUrlState`). Відкіт до поля без `onKeyDown` залишає
 * адресу незмінною після Enter, і тест падає.
 */
function LocationProbe(): null {
  const location = useLocation();

  (globalThis as { __search?: string }).__search = location.search;

  return null;
}

function respond(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response(JSON.stringify(null), { status: 404 })),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <MemoryRouter initialEntries={['/admin/jobs']}>
        <QueryClientProvider client={client}>
          <JobsPage />
          <LocationProbe />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  delete (globalThis as { __search?: string }).__search;
});

describe('JobsPage: Enter у полі ідентифікатора запускає ту саму дію, що й «Watch»', () => {
  it('Enter переносить ідентифікатор в адресу так само, як клік «Watch»', async () => {
    respond();
    const user = userEvent.setup();
    show();

    const input = screen.getByLabelText('⟦jobs.id⟧');
    await user.type(input, 'job-abc-123{Enter}');

    expect((globalThis as { __search?: string }).__search).toBe('?id=job-abc-123');
  });
});
