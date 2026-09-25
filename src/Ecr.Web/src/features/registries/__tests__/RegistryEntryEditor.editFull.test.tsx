import { describe, expect, it, vi, afterEach } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RegistryDefDto, RegistryEntryDto } from '@/api/types';
import { RegistryEntryEditor, entryBody } from '@/features/registries/RegistryEntryEditor';
import { testTheme } from '@/test/render';

/**
 * X-03 / R-04 (четвертий раунд UX, critical): правка запису довідника
 * затирала переклади назви — діалог підставляв лише `en` із рядка переліку, —
 * а поля показував порожніми, хоча значення в базі є.
 *
 * ⛔ Тепер діалог чекає `GET …/entries/{id}` і шле лише змінене: мова, якої
 * не чіпали, їде як була; стерта свідомо — порожнім рядком; незмінене
 * значення поля — не їде зовсім.
 */

const Registry: RegistryDefDto = {
  id: 11,
  code: 'CHEM',
  nameL10n: { values: { en: 'Chemicals' } },
  isHierarchical: false,
  isTemporal: false,
  sourceKind: 'Local',
  fields: [
    {
      id: 1,
      code: 'LIMIT',
      dataType: 'Decimal',
      isRequired: false,
      isScopeField: false,
      lookupRegistryDefId: null,
      nameL10n: { values: { en: 'Limit' } },
      unitId: null,
    },
    {
      id: 2,
      code: 'NOTE',
      dataType: 'String',
      isRequired: false,
      isScopeField: false,
      lookupRegistryDefId: null,
      nameL10n: { values: { en: 'Note' } },
      unitId: null,
    },
  ],
} as RegistryDefDto;

const Entry: RegistryEntryDto = {
  id: 7,
  code: 'PHENOL',
  display: 'Phenol',
  parentEntryId: null,
  validFrom: null,
  validTo: null,
};

const Detail = {
  id: 7,
  code: 'PHENOL',
  displayL10n: { values: { en: 'Phenol', ru: 'Фенол', kz: 'Фенол' } },
  parentEntryId: null,
  validFrom: null,
  validTo: null,
  values: { LIMIT: '12.5', NOTE: 'toxic' },
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function serve(): { posted: Record<string, unknown>[]; gets: string[] } {
  const posted: Record<string, unknown>[] = [];
  const gets: string[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.endsWith('/api/v1/languages')) {
        return json([
          { code: 'en', nameNative: 'English', isDefault: true },
          { code: 'ru', nameNative: 'Русский', isDefault: false },
          { code: 'kz', nameNative: 'Қазақша', isDefault: false },
        ]);
      }
      if (url.endsWith('/api/v1/registries/CHEM/entries/7')) {
        gets.push(url);
        return json(Detail);
      }
      if (url.endsWith('/api/v1/registries/CHEM/entries') && init?.method === 'POST') {
        posted.push(JSON.parse(String(init.body)) as Record<string, unknown>);
        return json({ id: 7 });
      }

      throw new Error(`Неочікуваний запит у тесті: ${url}`);
    }),
  );

  return { posted, gets };
}

function mount(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <RegistryEntryEditor registry={Registry} entry={Entry} opened onClose={() => {}} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RegistryEntryEditor: правка не затирає незмінене (X-03, R-04)', () => {
  it('діалог показує всі мови назви й значення полів із запису', async () => {
    const net = serve();
    mount();

    const ru = await screen.findByLabelText(/registries\.name⟧ · Русский/);
    expect((ru as HTMLInputElement).value).toBe('Фенол');
    expect(net.gets).toHaveLength(1);

    // ⛔ R-04: доти поля були порожні — `setValues({})`.
    expect((screen.getByLabelText(/^Limit/) as HTMLInputElement).value).toBe('12.5');
    expect((screen.getByLabelText(/^Note/) as HTMLInputElement).value).toBe('toxic');
  });

  it('зміна лише англійської назви везе решту мов як були, а незмінені поля — не везе', async () => {
    const net = serve();
    mount();

    const en = await screen.findByLabelText(/registries\.name⟧ · English/);
    fireEvent.change(en, { target: { value: 'Phenol (C6H5OH)' } });
    fireEvent.click(screen.getByRole('button', { name: '⟦common.save⟧' }));

    await waitFor(() => expect(net.posted).toHaveLength(1));
    const body = net.posted[0]!;

    expect(body['display']).toEqual({ values: { en: 'Phenol (C6H5OH)', ru: 'Фенол', kz: 'Фенол' } });
    expect(body['values']).toEqual({});
  });

  it('стерте поле їде як null, змінене — новим значенням', async () => {
    const net = serve();
    mount();

    const limit = await screen.findByLabelText(/^Limit/);
    fireEvent.change(limit, { target: { value: '15' } });
    fireEvent.change(screen.getByLabelText(/^Note/), { target: { value: '' } });
    fireEvent.click(screen.getByRole('button', { name: '⟦common.save⟧' }));

    await waitFor(() => expect(net.posted).toHaveLength(1));
    expect(net.posted[0]!['values']).toEqual({ LIMIT: '15', NOTE: null });
  });
});

describe('entryBody: мова, стерта свідомо, їде порожнім рядком', () => {
  it('сервер отримує сигнал «прибрати переклад», а не мовчазну відсутність', () => {
    const initial = { code: 'PHENOL', display: { en: 'Phenol', ru: 'Фенол' }, values: {} };
    const current = { code: 'PHENOL', display: { en: 'Phenol' }, values: {} };

    expect(entryBody(Registry, Entry, initial, current).display).toEqual({ values: { en: 'Phenol', ru: '' } });
  });
});
