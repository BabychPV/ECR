import { describe, expect, it, vi } from 'vitest';
import type { RegistryEntryDto } from '@/api/types';
import type { UsageResponse } from '@/features/registries/api';
import type { RegistryRowsPage, RegistryRowsQuery } from '@/features/registries/rows/api';
import {
  hasNamedReferences,
  loadEntryUsage,
  parseFieldLabel,
  type EntryUsageDeps,
} from '../entryUsage';

/**
 * Збирання звіту «де використовується» запису (ФВ-8.14) з наявних читань.
 *
 * ⛔ Предмет — що саме питається в сервера (поле, значення, дата) і що з відповіді потрапляє у
 * звіт. Помилка тут не видна ніде, крім неповного звіту — тобто хибного «посилань немає».
 */

const Entry = { id: 42, code: 'NOX' };

function entryDto(id: number, code: string, parentEntryId: number | null): RegistryEntryDto {
  return { id, code, display: code, parentEntryId, validFrom: null, validTo: null };
}

function page(ids: readonly number[], totalCount: number | null = ids.length): RegistryRowsPage {
  return {
    items: ids.map((id) => ({
      id,
      code: `R${String(id)}`,
      display: `Row ${String(id)}`,
      parentEntryId: null,
      validFrom: null,
      validTo: null,
      version: 'v',
      values: {},
    })),
    nextCursor: null,
    totalCount,
  } as RegistryRowsPage;
}

function deps(usage: UsageResponse, rows: EntryUsageDeps['rows']): EntryUsageDeps {
  return { usage: vi.fn(async () => usage), rows };
}

describe('parseFieldLabel', () => {
  it('ділить підпис поля за останньою крапкою', () => {
    expect(parseFieldLabel('PERMIT.SUBSTANCE')).toEqual({ registryCode: 'PERMIT', fieldCode: 'SUBSTANCE' });
    expect(parseFieldLabel('A.B.FIELD')).toEqual({ registryCode: 'A.B', fieldCode: 'FIELD' });
  });

  it('не вигадує довідник для підпису без обох частин', () => {
    expect(parseFieldLabel('NODOT')).toBeNull();
    expect(parseFieldLabel('.FIELD')).toBeNull();
    expect(parseFieldLabel('REG.')).toBeNull();
  });
});

describe('loadEntryUsage', () => {
  it('питає кожен довідник-власник поля саме цим полем і ідентифікатором запису', async () => {
    const calls: { code: string; query: RegistryRowsQuery }[] = [];
    const d = deps(
      {
        total: 2,
        items: [
          { kind: 'registryField', id: '7', label: 'PERMIT.SUBSTANCE', route: '/admin/registries/PERMIT/definition' },
          { kind: 'registryField', id: '8', label: 'LIMIT.POLLUTANT', route: null },
        ],
      },
      async (code, query) => {
        calls.push({ code, query });
        return page(code === 'PERMIT' ? [1, 2] : []);
      },
    );

    const report = await loadEntryUsage('SUBST', Entry, [], '2026-09-30', d);

    expect(calls).toEqual([
      { code: 'PERMIT', query: { asOf: '2026-09-30', fields: { SUBSTANCE: '42' }, limit: 20 } },
      { code: 'LIMIT', query: { asOf: '2026-09-30', fields: { POLLUTANT: '42' }, limit: 20 } },
    ]);
    expect(report.fields.map((g) => (g.status === 'ok' ? g.rows.map((r) => r.id) : 'error'))).toEqual([[1, 2], []]);
    expect(hasNamedReferences(report)).toBe(true);
  });

  it('відмова одного поля лишається помилкою цієї групи, а не «нуль посилань»', async () => {
    const refusal = new Error('404');
    const d = deps(
      {
        total: 2,
        items: [
          { kind: 'registryField', id: '7', label: 'HIDDEN.SUBSTANCE', route: null },
          { kind: 'registryField', id: '8', label: 'PERMIT.SUBSTANCE', route: null },
        ],
      },
      async (code) => {
        if (code === 'HIDDEN') throw refusal;
        return page([5]);
      },
    );

    const report = await loadEntryUsage('SUBST', Entry, [], '2026-09-30', d);

    expect(report.fields[0]).toMatchObject({ status: 'error', error: refusal });
    expect(report.fields[1]).toMatchObject({ status: 'ok', total: 1 });
  });

  it('відмова звіту рівня довідника валить увесь звіт', async () => {
    const d: EntryUsageDeps = {
      usage: vi.fn(async () => {
        throw new Error('403');
      }),
      rows: vi.fn(),
    };

    await expect(loadEntryUsage('SUBST', Entry, [], '2026-09-30', d)).rejects.toThrow('403');
    expect(d.rows).not.toHaveBeenCalled();
  });

  it('поле власного довідника не рахує сам запис як посилання на себе', async () => {
    const d = deps(
      { total: 1, items: [{ kind: 'registryField', id: '9', label: 'SUBST.SELF', route: null }] },
      async () => page([42, 43], 2),
    );

    const report = await loadEntryUsage('SUBST', Entry, [], '2026-09-30', d);

    expect(report.fields[0]).toMatchObject({ status: 'ok', total: 1 });
    expect(report.fields[0]!.status === 'ok' && report.fields[0]!.rows.map((r) => r.id)).toEqual([43]);
  });

  it('загальна кількість — з сервера, а не довжина сторінки', async () => {
    const d = deps(
      { total: 1, items: [{ kind: 'registryField', id: '7', label: 'PERMIT.SUBSTANCE', route: null }] },
      async () => page([1, 2], 57),
    );

    const report = await loadEntryUsage('SUBST', Entry, [], '2026-09-30', d);

    expect(report.fields[0]).toMatchObject({ status: 'ok', total: 57 });
  });

  it('речовини — лише ті, що вказують саме на цей запис; дочірні — з записів довідника', async () => {
    const d = deps(
      {
        total: 4,
        items: [
          { kind: 'methodologySubstance', id: '100', label: 'NOX', route: '/admin/methodologies/3/versions' },
          { kind: 'methodologySubstance', id: '101', label: 'SO2', route: '/admin/methodologies/4/versions' },
          { kind: 'templateColumn', id: '5', label: 'EMISSIONS.SUBSTANCE', route: '/admin/templates/1/versions/2' },
          { kind: 'data', id: 'cells', label: 'Values in document cells', route: null },
        ],
      },
      vi.fn(),
    );
    const siblings = [entryDto(42, 'NOX', null), entryDto(43, 'NO', 42), entryDto(44, 'NO2', 42), entryDto(45, 'SO2', null)];

    const report = await loadEntryUsage('SUBST', Entry, siblings, '2026-09-30', d);

    expect(report.substances).toEqual([{ id: '100', route: '/admin/methodologies/3/versions' }]);
    expect(report.children.map((c) => c.code)).toEqual(['NO', 'NO2']);
    expect(report.columns).toEqual([{ id: '5', label: 'EMISSIONS.SUBSTANCE', route: '/admin/templates/1/versions/2' }]);
    expect(report.dataInDocuments).toBe(true);
    expect(report.truncated).toBe(false);
  });

  it('обрізаний сервером перелік довідника позначається, а колонки не вважаються посиланням на запис', async () => {
    const d = deps(
      { total: 30, items: [{ kind: 'templateColumn', id: '5', label: 'T.C', route: null }] },
      vi.fn(),
    );

    const report = await loadEntryUsage('SUBST', Entry, [], '2026-09-30', d);

    expect(report.truncated).toBe(true);
    expect(hasNamedReferences(report)).toBe(false);
  });
});
