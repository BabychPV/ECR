import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { UnitsPage } from '@/pages/admin/UnitsPage';
import { testTheme } from '@/test/render';

/**
 * BE-15 ч.2: правка одиниці виміру з переліку.
 *
 * ⛔ Кожен сценарій ходить через `fetch` — тобто через справжні `getUnit`,
 * `unitUsage`, `updateUnit` і розбір відмов `apiFetch`, а не заглушки функцій.
 */

const SeededStrings: Record<string, string> = {
  'units.title': 'Units of measure',
  'units.code': 'Unit',
  'units.factor': 'Factor to base',
  'units.offset': 'Offset to base',
  'units.symbol': 'Symbol',
  'units.name': 'Name',
  'units.factorHint': 'Multiplier to the base unit.',
  'units.offsetHint': 'Only nonzero for temperature units.',
  'units.edit': 'Edit',
  'units.editTitle': 'Edit unit {code}',
  'units.codeFixed': 'Code, dimension and the base flag cannot be changed.',
  'units.factorLockedBase': 'Base unit: factor and offset are fixed.',
  'units.factorLockedChecking': 'Checking where the unit is used...',
  'units.factorLockedUnknown': 'Could not check where the unit is used.',
  'units.factorLockedUsed': 'Referenced in {total} place(s): factor and offset are fixed.',
  'units.reloadCurrent': 'Take the current version',
  'units.saved': 'Unit saved.',
  'err.ECR-UOM-0409.unitChanged': 'Someone else changed unit "{code}".',
  'err.ECR-UOM-0409.unitFactorInUse': 'Unit "{code}" is referenced in {total} place(s).',
  'common.cancel': 'Cancel',
  'common.delete': 'Remove',
  'common.save': 'Save',
};

type Usage = { total: number; items: unknown[] } | 'pending' | 'error';

interface Put {
  ifMatch: string | null;
  body: {
    symbolL10n: Record<string, string>;
    nameL10n: Record<string, string>;
    factorToBase: string;
    offsetToBase: string;
  };
}

interface Api {
  puts: Put[];
  listCalls: () => number;
}

const lb = {
  id: 5,
  code: 'lb',
  symbolL10n: { en: 'lb', ru: 'фунт' },
  nameL10n: { en: 'Pound', ru: 'Фунт' },
  dimensionId: 1,
  isBase: false,
  factorToBase: '0.4535923700',
  offsetToBase: '0.0000000000',
  rowVersion: 'v1',
};

const kg = {
  ...lb,
  id: 1,
  code: 'kg',
  symbolL10n: { en: 'kg' },
  nameL10n: { en: 'Kilogram' },
  isBase: true,
  factorToBase: '1.0000000000',
};

function problem(status: number, body: Record<string, unknown>): Response {
  return new Response(JSON.stringify({ status, title: 'Refused', ...body }), {
    status,
    headers: { 'Content-Type': 'application/problem+json' },
  });
}

function mockApi(options: { usage: Usage; puts?: ((put: Put) => Response)[] }): Api {
  const puts: Put[] = [];
  const replies = [...(options.puts ?? [])];
  let list = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      const json = (body: unknown, status = 200): Response =>
        new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
      }

      if (url.endsWith('/api/v1/me')) {
        return json({
          userId: 1,
          userName: 'bootstrap',
          language: 'en',
          permissions: ['Uom.EditCatalog'],
          isSimulation: false,
          denies: [],
          grants: {},
          mustChangePassword: false,
          simulatedForUserId: null,
        });
      }

      if (url.endsWith('/api/v1/units/5/usage')) {
        if (options.usage === 'pending') return new Promise<Response>(() => {});
        if (options.usage === 'error') return problem(500, { errorCode: 'ECR-SYS-0500' });
        return json(options.usage);
      }

      if (url.endsWith('/api/v1/units/5') && method === 'PUT') {
        const put: Put = {
          ifMatch: new Headers(init?.headers).get('If-Match'),
          body: JSON.parse(String(init?.body)) as Put['body'],
        };
        puts.push(put);
        const reply = replies.shift();
        return reply === undefined ? json({ ...lb, ...put.body, rowVersion: 'v9' }) : reply(put);
      }

      if (url.endsWith('/api/v1/units/5')) return json(lb);
      if (url.endsWith('/api/v1/units/1')) return json(kg);

      if (url.endsWith('/api/v1/units')) {
        list += 1;
        return json(
          [kg, lb].map((unit) => ({
            id: unit.id,
            code: unit.code,
            dimensionId: 1,
            dimensionCode: 'Mass',
            factorToBase: unit.factorToBase,
            offsetToBase: unit.offsetToBase,
          })),
        );
      }

      throw new Error(`No mock for ${method} ${url}`);
    }),
  );

  return { puts, listCalls: () => list };
}

async function openEdit(code = 'lb'): Promise<HTMLElement> {
  await loadCatalog('en', 'private');

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <UnitsPage />
      </QueryClientProvider>
    </MantineProvider>,
  );

  fireEvent.click(await screen.findByRole('button', { name: `Edit ${code}` }));

  const dialog = await screen.findByRole('dialog');
  await within(dialog).findByLabelText(/^Symbol/);
  return dialog;
}

function field(dialog: HTMLElement, label: string): HTMLInputElement {
  return within(dialog).getByLabelText(new RegExp(`^${label}`)) as HTMLInputElement;
}

/** Текст, на який посилається `aria-describedby` поля. */
function describedText(input: HTMLElement): string {
  const ids = (input.getAttribute('aria-describedby') ?? '').split(/\s+/).filter(Boolean);
  return ids.map((id) => document.getElementById(id)?.textContent ?? '').join(' ');
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('UnitEditModal: правка одиниці (BE-15 ч.2)', () => {
  it('невикористовувана небазова одиниця: множник і зсув активні, 20 значущих цифр доїжджають рядком', async () => {
    const api = mockApi({ usage: { total: 0, items: [] } });
    const dialog = await openEdit();

    const factor = field(dialog, 'Factor to base');
    await waitFor(() => {
      expect(factor.disabled).toBe(false);
    });
    expect(field(dialog, 'Offset to base').disabled).toBe(false);
    expect(factor.value).toBe('0.4535923700');

    // Двадцять значущих цифр при 13 знаках після коми: Mantine `NumberInput`
    // тримає рядком лише значення з ≥ 14 знаками дробу, а це проганяє через
    // `floatValue` (IEEE-754) і лишає ~17 цифр. `blur` — там, де він ще й
    // нормалізує введене.
    fireEvent.focus(factor);
    fireEvent.change(factor, { target: { value: '1234567.8901234567891' } });
    fireEvent.blur(factor);
    expect(field(dialog, 'Factor to base').value).toBe('1234567.8901234567891');
    fireEvent.change(field(dialog, 'Symbol'), { target: { value: 'lbs' } });
    const lists = api.listCalls();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(api.puts).toHaveLength(1);
    });
    expect(api.puts[0]?.ifMatch).toBe('"v1"');
    expect(api.puts[0]?.body.factorToBase).toBe('1234567.8901234567891');
    expect(api.puts[0]?.body.offsetToBase).toBe('0');
    // Інші мови не губляться.
    expect(api.puts[0]?.body.symbolL10n).toEqual({ en: 'lbs', ru: 'фунт' });
    expect(api.puts[0]?.body.nameL10n).toEqual({ en: 'Pound', ru: 'Фунт' });

    // Успіх: діалог закрито, перелік перечитано.
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    expect(api.listCalls()).toBeGreaterThan(lists);
  });

  it('використовувана одиниця: множник і зсув вимкнені заздалегідь, причина видна й прив’язана', async () => {
    const api = mockApi({ usage: { total: 3, items: [] } });
    const dialog = await openEdit();

    const factor = field(dialog, 'Factor to base');
    await waitFor(() => {
      expect(describedText(factor)).toContain('Referenced in 3 place(s)');
    });
    expect(factor.disabled).toBe(true);
    expect(field(dialog, 'Offset to base').disabled).toBe(true);
    expect(within(dialog).getAllByText(/Referenced in 3 place\(s\)/).length).toBeGreaterThan(0);

    // Позначення зберігається, множник іде чинний.
    fireEvent.change(field(dialog, 'Name'), { target: { value: 'Pound (avdp)' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
    await waitFor(() => {
      expect(api.puts).toHaveLength(1);
    });
    expect(api.puts[0]?.body.factorToBase).toBe('0.45359237');
  });

  it('поки перелік посилань вантажиться — множник вимкнений', async () => {
    mockApi({ usage: 'pending' });
    const dialog = await openEdit();

    const factor = field(dialog, 'Factor to base');
    expect(factor.disabled).toBe(true);
    expect(describedText(factor)).toContain('Checking where the unit is used');
  });

  it('перелік посилань відмовив — множник вимкнений з причиною', async () => {
    mockApi({ usage: 'error' });
    const dialog = await openEdit();

    const factor = field(dialog, 'Factor to base');
    await waitFor(() => {
      expect(describedText(factor)).toContain('Could not check where the unit is used');
    });
    expect(factor.disabled).toBe(true);
  });

  it('базова одиниця — множник вимкнений', async () => {
    mockApi({ usage: { total: 0, items: [] } });
    const dialog = await openEdit('kg');

    const factor = field(dialog, 'Factor to base');
    expect(factor.disabled).toBe(true);
    expect(describedText(factor)).toContain('Base unit: factor and offset are fixed.');
  });

  it('409 unitChanged: чернетка лишається, «взяти свіжу» шле версію з відмови', async () => {
    const api = mockApi({
      usage: { total: 0, items: [] },
      puts: [
        () =>
          problem(409, {
            errorCode: 'ECR-UOM-0409',
            messageKey: 'err.ECR-UOM-0409.unitChanged',
            code: 'lb',
            rowVersion: 'v2',
          }),
      ],
    });
    const dialog = await openEdit();

    fireEvent.change(field(dialog, 'Symbol'), { target: { value: 'lbm' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    const takeFresh = await within(dialog).findByRole('button', { name: 'Take the current version' });
    expect(within(dialog).getByText('Someone else changed unit "lb".')).toBeTruthy();
    expect(field(dialog, 'Symbol').value).toBe('lbm');
    expect(within(dialog).getByRole('button', { name: 'Save' })).toHaveProperty('disabled', true);
    expect(within(dialog).queryByRole('alert')).toBeNull();

    fireEvent.click(takeFresh);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(api.puts).toHaveLength(2);
    });
    expect(api.puts[1]?.ifMatch).toBe('"v2"');
    expect(api.puts[1]?.body.symbolL10n['en']).toBe('lbm');
  });

  it('409 unitFactorInUse: причина з кількістю посилань, не загальна помилка; поля заблоковані', async () => {
    mockApi({
      usage: { total: 0, items: [] },
      puts: [
        () =>
          problem(409, {
            errorCode: 'ECR-UOM-0409',
            messageKey: 'err.ECR-UOM-0409.unitFactorInUse',
            code: 'lb',
            total: '4',
            references: [],
          }),
      ],
    });
    const dialog = await openEdit();

    const factor = field(dialog, 'Factor to base');
    await waitFor(() => {
      expect(factor.disabled).toBe(false);
    });
    fireEvent.change(factor, { target: { value: '0.5' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    expect(await within(dialog).findByText('Unit "lb" is referenced in 4 place(s).')).toBeTruthy();
    expect(within(dialog).queryByRole('alert')).toBeNull();
    expect(field(dialog, 'Factor to base').disabled).toBe(true);
    expect(describedText(field(dialog, 'Factor to base'))).toContain('Referenced in 4 place(s)');
  });

  it('422 factorMustBePositive — текстом сервера під полем множника, не загальна помилка', async () => {
    mockApi({
      usage: { total: 0, items: [] },
      puts: [
        () =>
          problem(422, {
            title: 'Invalid unit conversion',
            detail: 'The factor to the base unit of unit "lb" must be greater than zero, not 0.',
            errorCode: 'ECR-UOM-0422',
            messageKey: 'err.ECR-UOM-0422.factorMustBePositive',
            code: 'lb',
            factorToBase: '0',
          }),
      ],
    });
    const dialog = await openEdit();

    const factor = field(dialog, 'Factor to base');
    await waitFor(() => {
      expect(factor.disabled).toBe(false);
    });
    fireEvent.change(factor, { target: { value: '0' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(describedText(field(dialog, 'Factor to base'))).toContain(
        'The factor to the base unit of unit "lb" must be greater than zero, not 0.',
      );
    });
    expect(within(dialog).queryByRole('alert')).toBeNull();
  });

  it('422 з іншою причиною (incompatibleDimensions) — ErrorAlert, а не під полем множника', async () => {
    const detail = 'Units "lb" and "m" measure different things.';
    mockApi({
      usage: { total: 0, items: [] },
      puts: [
        () =>
          problem(422, {
            title: 'Invalid unit conversion',
            detail,
            errorCode: 'ECR-UOM-0422',
            messageKey: 'err.ECR-UOM-0422.incompatibleDimensions',
          }),
      ],
    });
    const dialog = await openEdit();

    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    const alert = await within(dialog).findByRole('alert');
    expect(alert.textContent).toContain(detail);
    expect(describedText(field(dialog, 'Factor to base'))).not.toContain(detail);
    expect(field(dialog, 'Factor to base').getAttribute('aria-invalid')).not.toBe('true');
  });

  it('решта відмов — через ErrorAlert', async () => {
    mockApi({
      usage: { total: 0, items: [] },
      puts: [() => problem(403, { errorCode: 'ECR-AUTH-0403' })],
    });
    const dialog = await openEdit();

    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    expect(await within(dialog).findByRole('alert')).toBeTruthy();
  });
});
