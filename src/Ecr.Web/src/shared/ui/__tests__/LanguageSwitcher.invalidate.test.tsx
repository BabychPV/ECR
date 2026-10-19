import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query';
import { useEffect, type JSX } from 'react';
import { LanguageSwitcher, resetLanguageCoverage } from '@/shared/ui/LanguageSwitcher';
import { useCatalog } from '@/shared/i18n/useCatalog';
import { language, loadCatalog, setLanguage } from '@/shared/i18n';

/**
 * CL-29: ключі React Query серверних відповідей з локалізованим текстом не містять мови, тож після
 * перемикання лишалися відповіді старою мовою (staleTime 30 с). Перемикач має перезапитати
 * активні запити `health`/`jobs`/`jobs-summary`/`job` — вже з новою мовою — і не чіпати решту.
 */
const LOCALIZED: readonly (readonly unknown[])[] = [
  ['health', 'db'],
  ['health', 'ready'],
  ['jobs', false],
  ['jobs-summary', false],
  ['job', 'j#1'],
];

const OTHER_KEY = ['periods', 'list'] as const;

const seen = new Map<string, string[]>();

function record(key: readonly unknown[]): string {
  const id = JSON.stringify(key);
  seen.set(id, [...(seen.get(id) ?? []), language()]);
  return id;
}

function routes(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      const body = url.includes('/api/v1/languages')
        ? [
            { code: 'en', nameNative: 'English', isDefault: true },
            { code: 'kz', nameNative: 'Қазақша', isDefault: false },
          ]
        : url.includes('/ui-strings/kz')
          ? { languageCode: 'kz', revision: 1, strings: { 'test.marker': 'kz-loaded' } }
          : { languageCode: 'en', revision: 1, strings: { 'test.marker': 'en-loaded' } };

      return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

function Probe({ queryKey }: { queryKey: readonly unknown[] }): null {
  // staleTime нескінченний: самі по собі запити не перезапитуються — лише від інвалідації.
  useQuery({ queryKey, queryFn: async () => record(queryKey), staleTime: Infinity });
  return null;
}

function Page(): JSX.Element {
  useCatalog();

  useEffect(() => {
    void loadCatalog('en', 'private');
  }, []);

  return (
    <>
      <LanguageSwitcher />
      {[...LOCALIZED, OTHER_KEY].map((key) => (
        <Probe key={JSON.stringify(key)} queryKey={key} />
      ))}
    </>
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  resetLanguageCoverage();
  localStorage.clear();
  setLanguage('en');
  seen.clear();
});

describe('Перемикач мови інвалідує локалізовані серверні запити (CL-29)', () => {
  it('після зміни мови health/jobs перезапитуються з новою мовою, решта кешу не чіпається', async () => {
    routes();
    render(
      <MantineProvider>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Page />
        </QueryClientProvider>
      </MantineProvider>,
    );

    // Початкове завантаження — англійською, по одному разу.
    await waitFor(() => expect(seen.size).toBe(LOCALIZED.length + 1));
    for (const [id, langs] of seen) expect({ id, langs }).toEqual({ id, langs: ['en'] });

    fireEvent.change(await screen.findByLabelText('⟦profile.language⟧'), { target: { value: 'kz' } });
    expect(language()).toBe('kz');

    // ⛔ Мутація «прибрати цикл invalidateQueries в applyLanguage» — повторних запитів немає: тест червоний.
    for (const key of LOCALIZED) {
      const id = JSON.stringify(key);
      await waitFor(() => expect({ id, langs: seen.get(id) }).toEqual({ id, langs: ['en', 'kz'] }));
    }

    // Чужий ключ не перезапитано.
    expect(seen.get(JSON.stringify(OTHER_KEY))).toEqual(['en']);
  });
});
