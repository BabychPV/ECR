import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  deletePermitBinding,
  getPermitBinding,
  listPermitBindings,
  previewPermitBinding,
  savePermitBinding,
} from '@/features/permits/api/bindingsApi';
import type { SavePermitBindingRequest } from '@/features/permits/api/bindingsTypes';

/**
 * Споживач API прив'язок аркуша до дозволів (Permits, PM-09): адреса, метод і тіло кожної дії. Екрана ще немає (PM-11) —
 * сторож «кожна дія сервера має споживача» бачить літерали, але не бачить, ЩО відправлено.
 */

interface Attempt {
  url: string;
  method: string;
  body: unknown;
}

function mockFetch(status = 200, body: unknown = {}): Attempt[] {
  const attempts: Attempt[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      attempts.push({
        url: String(input),
        method: (init?.method ?? 'GET').toUpperCase(),
        body: typeof init?.body === 'string' ? JSON.parse(init.body) : null,
      });

      return status === 204
        ? new Response(null, { status })
        : new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
    }),
  );

  return attempts;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('API прив’язок аркуша до дозволів', () => {
  it('перелік і читання кодують код прив’язки в адресі версії', async () => {
    const attempts = mockFetch(200, { isEditable: true, items: [] });

    await listPermitBindings(42);
    await getPermitBinding(42, 'A/B');

    expect(attempts[0]).toMatchObject({ url: '/api/v1/template-versions/42/permit-bindings', method: 'GET' });
    expect(attempts[1]).toMatchObject({ url: '/api/v1/template-versions/42/permit-bindings/A%2FB', method: 'GET' });
  });

  it('запис шле PUT з тілом, видалення — DELETE без тіла', async () => {
    const attempts = mockFetch();
    const body: SavePermitBindingRequest = {
      sheetTableDefId: 7,
      permitTypeCode: 'AIR',
      permitTableCode: 'Emissions',
      mode: 'Rows',
      selectionRule: 'ByFacilityPeriodEnd',
      headerFieldDefId: null,
      isActive: true,
      columns: [{ role: 'RowKey', permitFieldCode: 'Substance', sheetColumnDefId: 11 }],
    };

    await savePermitBinding(42, 'EMISSIONS', body);
    await deletePermitBinding(42, 'EMISSIONS');

    expect(attempts[0]).toMatchObject({ url: '/api/v1/template-versions/42/permit-bindings/EMISSIONS', method: 'PUT', body });
    expect(attempts[1]).toMatchObject({ url: '/api/v1/template-versions/42/permit-bindings/EMISSIONS', method: 'DELETE', body: null });
  });

  it('перегляд без параметрів кличе адресу без запиту, з параметрами — кодує лише задані', async () => {
    const attempts = mockFetch();

    await previewPermitBinding(42, 'EMISSIONS');
    await previewPermitBinding(42, 'EMISSIONS', {
      documentId: 9001,
      periodKey: 202606,
      facilityName: 'Site A & B',
      periodEnd: '2026-06-30',
      permitEntryId: 5,
    });
    await previewPermitBinding(42, 'EMISSIONS', { documentId: 0 });

    expect(attempts[0]!.url).toBe('/api/v1/template-versions/42/permit-bindings/EMISSIONS/preview');
    expect(attempts[1]!.url).toBe(
      '/api/v1/template-versions/42/permit-bindings/EMISSIONS/preview?documentId=9001&periodKey=202606&facilityName=Site+A+%26+B&periodEnd=2026-06-30&permitEntryId=5',
    );
    expect(attempts[2]!.url).toBe('/api/v1/template-versions/42/permit-bindings/EMISSIONS/preview?documentId=0');
  });
});
