import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RegistryDefDto } from '@/api/types';
import { EcrApiError } from '@/api/client';
import { RegistryEntryEditor, isEntryNameRequired } from '@/features/registries/RegistryEntryEditor';
import { showApiError } from '@/shared/ui/notify';
import { testTheme } from '@/test/render';

vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showApiError: vi.fn(),
  showDone: vi.fn(),
}));

/**
 * D14 (AU-14): сервер відхиляє запис довідника без назви `422 ECR-REG-4222 entryNameRequired`.
 *
 * ⛔ Відмова належить полю назви: текст каталогу (`detail` за `messageKey`) стоїть під полем з `role="alert"`, а не
 * зникаючим сповіщенням; правка назви знімає його. Інші відмови — як і раніше, сповіщенням.
 */
const Registry = {
  id: 11,
  code: 'CHEM',
  nameL10n: { values: { en: 'Chemicals' } },
  isHierarchical: false,
  isTemporal: false,
  sourceKind: 'Local',
  fields: [],
} as unknown as RegistryDefDto;

const NameRequiredDetail = 'Enter the entry name in at least one language.';

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function serve(rejection: { messageKey: string; errorCode: string; detail: string }): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.endsWith('/api/v1/languages')) {
        return json([
          { code: 'en', nameNative: 'English', isDefault: true },
          { code: 'ru', nameNative: 'Русский', isDefault: false },
        ]);
      }
      if (url.endsWith('/api/v1/registries/CHEM/entries') && init?.method === 'POST') {
        return json(
          {
            type: 'about:blank',
            title: 'Rejected',
            status: 422,
            detail: rejection.detail,
            errorCode: rejection.errorCode,
            correlationId: 'c-1',
            messageKey: rejection.messageKey,
          },
          422,
        );
      }

      throw new Error(`Неочікуваний запит у тесті: ${url}`);
    }),
  );
}

function mount(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <RegistryEntryEditor registry={Registry} entry={null} opened onClose={() => undefined} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function fillAndSave(): Promise<void> {
  fireEvent.change(await screen.findByRole('textbox', { name: /code/i }), { target: { value: 'E1' } });
  fireEvent.change(await screen.findByRole('textbox', { name: /English/ }), { target: { value: 'Alpha' } });
  fireEvent.click(screen.getByRole('button', { name: /save/i }));
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(showApiError).mockClear();
});

describe('RegistryEntryEditor: 422 ECR-REG-4222 — назва запису обов’язкова (D14)', () => {
  it('показує відмову під полем назви текстом каталогу, без сповіщення; правка назви її знімає', async () => {
    serve({ messageKey: 'err.ECR-REG-4222.entryNameRequired', errorCode: 'ECR-REG-4222', detail: NameRequiredDetail });
    mount();

    await fillAndSave();

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toBe(NameRequiredDetail);
    expect(alert.getAttribute('data-registry-entry')).toBe('name-required');
    expect(showApiError).not.toHaveBeenCalled();

    fireEvent.change(screen.getByRole('textbox', { name: /English/ }), { target: { value: 'Alpha 2' } });
    await waitFor(() => expect(screen.queryByText(NameRequiredDetail)).toBeNull());
  });

  it('інша відмова — сповіщенням, під полем назви нічого', async () => {
    serve({ messageKey: 'err.ECR-REG-0409.entryCodeTaken', errorCode: 'ECR-REG-0409', detail: 'Code taken.' });
    mount();

    await fillAndSave();

    await waitFor(() => expect(showApiError).toHaveBeenCalledTimes(1));
    expect(screen.queryByText('Code taken.')).toBeNull();
    expect(document.querySelector('[data-registry-entry="name-required"]')).toBeNull();
  });

  it('isEntryNameRequired розпізнає лише ключ entryNameRequired', () => {
    const problem = (messageKey: string): EcrApiError =>
      new EcrApiError({
        type: 'about:blank',
        title: 't',
        status: 422,
        detail: 'd',
        errorCode: 'ECR-REG-4222',
        correlationId: 'c',
        extensions2: { messageKey },
      });

    expect(isEntryNameRequired(problem('err.ECR-REG-4222.entryNameRequired'))).toBe(true);
    expect(isEntryNameRequired(problem('err.ECR-REG-0422.entryCodeAutomatic'))).toBe(false);
    expect(isEntryNameRequired(new Error('x'))).toBe(false);
  });
});
