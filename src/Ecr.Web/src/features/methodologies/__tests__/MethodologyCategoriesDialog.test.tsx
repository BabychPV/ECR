import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { t } from '@/shared/i18n';
import { testTheme } from '@/test/render';
import { MethodologyCategoriesDialog } from '../MethodologyCategoriesDialog';

/**
 * План CONST-CATEGORY: діалог переліку категорій версії.
 *
 * ⛔ Перевіряється те, що сервер інакше відхилив би або що тихо зламало б зіставлення: зайняту
 * категорію не можна прибрати чи вивести з ужитку (`409 categoryInUse`), коду не перейменовують (поле
 * коду в режимі правки недоступне), запис іде `PUT …/categories/{code}`, прибирання — `DELETE`.
 */

const Base = '/api/v1/methodologies/1/versions/2/categories';

const List = [
  { id: 1, code: 'Diesel', description: 'Diesel engines', constantCount: 4, isRetired: false, ordinal: 1 },
  { id: 2, code: 'Gas', description: null, constantCount: 0, isRetired: false, ordinal: 2 },
];

let calls: { url: string; method: string; body: unknown }[] = [];

function mockApi(): void {
  calls = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      calls.push({ url, method, body: init?.body === undefined ? undefined : JSON.parse(String(init.body)) });

      if (method === 'DELETE') return new Response(null, { status: 204 });
      if (method === 'PUT') {
        return new Response(JSON.stringify({ ...List[1], code: 'Coal' }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      if (url === Base) {
        return new Response(JSON.stringify(List), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      throw new Error(`Немає мока для ${url}`);
    }),
  );
}

function show(editable: boolean): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MethodologyCategoriesDialog methodologyId={1} versionId={2} editable={editable} opened onClose={() => {}} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function rowOf(code: string): HTMLElement {
  const row = document.querySelector<HTMLElement>(`tr[data-category-code="${code}"]`);
  if (row === null) throw new Error(`рядка ${code} немає`);
  return row;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const Slow = 60_000;

describe('MethodologyCategoriesDialog', () => {
  it(
    'зайняту категорію не прибрати й не вивести з ужитку; вільну — прибрати після підтвердження',
    async () => {
      mockApi();
      show(true);

      await screen.findByText('Diesel', {}, { timeout: Slow });

      const used = within(rowOf('Diesel'));
      expect((used.getByRole('button', { name: t('methodologies.deleteFormula') }) as HTMLButtonElement | HTMLInputElement).disabled).toBe(true);
      expect((used.getByRole('button', { name: t('methodologies.constCategories.retire') }) as HTMLButtonElement | HTMLInputElement).disabled).toBe(true);

      const free = within(rowOf('Gas'));
      fireEvent.click(free.getByRole('button', { name: t('methodologies.deleteFormula') }));
      // ⚠ Двокрокове: перший клік лише питає (InlineConfirm макета), запиту ще немає.
      expect(calls.some((c) => c.method === 'DELETE')).toBe(false);
      fireEvent.click(free.getByRole('button', { name: t('methodologies.constCategories.confirmDelete') }));

      await waitFor(() => {
        expect(calls.find((c) => c.method === 'DELETE')?.url).toBe(`${Base}/Gas`);
      });
    },
    Slow,
  );

  it(
    'додавання — PUT на код; правка опису не дає змінити код',
    async () => {
      mockApi();
      show(true);

      await screen.findByText('Diesel', {}, { timeout: Slow });

      fireEvent.change(screen.getByLabelText(t('methodologies.code')), { target: { value: ' Coal ' } });
      fireEvent.click(screen.getByRole('button', { name: t('methodologies.constCategories.add') }));

      await waitFor(() => {
        const put = calls.find((c) => c.method === 'PUT');
        expect(put?.url).toBe(`${Base}/Coal`);
        expect(put?.body).toEqual({ description: null, isRetired: null, ordinal: null });
      });

      fireEvent.click(within(rowOf('Diesel')).getByRole('button', { name: t('methodologies.constCategories.editAria', { code: 'Diesel' }) }));
      expect((screen.getByLabelText(t('methodologies.code')) as HTMLInputElement).disabled).toBe(true);
      expect((screen.getByLabelText(t('methodologies.code')) as HTMLInputElement).value).toBe('Diesel');
    },
    Slow,
  );

  it(
    'без права правки — лише перегляд, жодної дії',
    async () => {
      mockApi();
      show(false);

      await screen.findByText('Diesel', {}, { timeout: Slow });
      expect(screen.queryByRole('button', { name: t('methodologies.constCategories.add') })).toBeNull();
      expect(screen.getByText(t('methodologies.constCategories.readOnly'))).toBeTruthy();
    },
    Slow,
  );
});
