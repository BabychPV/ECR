import { afterEach, describe, expect, it, vi } from 'vitest';
import { columnOrderPatch, moveItem, ordinalChanges, orderPatch } from '../reorder';
import { reorderColumns } from '../reorderApi';

/**
 * Перестановка колонок конструктора (`ФВ-2.6`): що саме їде в
 * `PATCH …/presentation`.
 *
 * ⛔ Мутаційний доказ (перевірено руками 2026-09-30): у `ordinalChanges`
 * прибрати фільтр `ordinal !== item.ordinal` — червоніє «лише змінені»;
 * замінити `sorted[index]` на `index` — червоніє «дірки зберігаються»;
 * прибрати гілку `unique` — червоніє «повтори вирівнюються»; у
 * `reorderColumns` прибрати ранній `return null` — червоніє «порожній патч не
 * шлеться».
 */
describe('moveItem', () => {
  it('переносить елемент уперед і назад', () => {
    expect(moveItem(['a', 'b', 'c', 'd'], 0, 2)).toEqual(['b', 'c', 'a', 'd']);
    expect(moveItem(['a', 'b', 'c', 'd'], 3, 1)).toEqual(['a', 'd', 'b', 'c']);
  });

  it('позиція поза списком — без змін', () => {
    expect(moveItem(['a', 'b'], 0, 5)).toEqual(['a', 'b']);
    expect(moveItem(['a', 'b'], -1, 0)).toEqual(['a', 'b']);
  });
});

describe('ordinalChanges', () => {
  const columns = [
    { id: 1, ordinal: 0 },
    { id: 2, ordinal: 1 },
    { id: 3, ordinal: 2 },
    { id: 4, ordinal: 3 },
  ];

  it('лише змінені колонки: сусіди поза діапазоном не зачіпаються', () => {
    expect(ordinalChanges(columns, 1, 2)).toEqual([
      { id: 3, ordinal: 1 },
      { id: 2, ordinal: 2 },
    ]);
  });

  it('дірки в нумерації зберігаються', () => {
    const gapped = [
      { id: 1, ordinal: 10 },
      { id: 2, ordinal: 20 },
      { id: 3, ordinal: 30 },
    ];

    expect(ordinalChanges(gapped, 2, 0)).toEqual([
      { id: 3, ordinal: 10 },
      { id: 1, ordinal: 20 },
      { id: 2, ordinal: 30 },
    ]);
  });

  it('повтори вирівнюються до 0…n−1, інакше перестановка непомітна', () => {
    const tied = [
      { id: 1, ordinal: 0 },
      { id: 2, ordinal: 0 },
      { id: 3, ordinal: 0 },
    ];

    expect(ordinalChanges(tied, 0, 1)).toEqual([
      { id: 1, ordinal: 1 },
      { id: 3, ordinal: 2 },
    ]);
  });

  it('те саме місце — нічого не змінюється', () => {
    expect(ordinalChanges(columns, 2, 2)).toEqual([]);
  });
});

describe('columnOrderPatch', () => {
  it('поле Ordinal колонки, значення рядком — як приймає сервер', () => {
    expect(columnOrderPatch([{ id: 7, ordinal: 3 }])).toEqual([
      { entityType: 'ColumnDef', entityId: 7, field: 'Ordinal', value: '3' },
    ]);
  });
});

describe('orderPatch', () => {
  it('RowDef: той самий пакет, інший entityType', () => {
    expect(orderPatch('RowDef', [{ id: 9, ordinal: 0 }])).toEqual([
      { entityType: 'RowDef', entityId: 9, field: 'Ordinal', value: '0' },
    ]);
  });
});

describe('reorderColumns', () => {
  afterEach(() => vi.unstubAllGlobals());

  function mockServer(): { url: string; method: string; body: unknown }[] {
    const calls: { url: string; method: string; body: unknown }[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string, init?: RequestInit) => {
        calls.push({
          url,
          method: init?.method ?? 'GET',
          body: typeof init?.body === 'string' ? (JSON.parse(init.body) as unknown) : undefined,
        });
        return new Response(JSON.stringify({ presentationRevision: 5 }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }),
    );
    return calls;
  }

  it('один PATCH презентаційного шару з усіма змінами разом', async () => {
    const calls = mockServer();
    const columns = [
      { id: 1, ordinal: 0 },
      { id: 2, ordinal: 1 },
    ];

    await expect(reorderColumns(9, columns, 1, 0)).resolves.toBe(5);

    expect(calls).toHaveLength(1);
    expect(calls[0]?.method).toBe('PATCH');
    expect(calls[0]?.url).toContain('/api/v1/template-versions/9/presentation');
    expect(calls[0]?.body).toEqual([
      { entityType: 'ColumnDef', entityId: 2, field: 'Ordinal', value: '0' },
      { entityType: 'ColumnDef', entityId: 1, field: 'Ordinal', value: '1' },
    ]);
  });

  it('порожній патч не шлеться: сервер відхилив би його ECR-TMPL-0422', async () => {
    const calls = mockServer();

    await expect(reorderColumns(9, [{ id: 1, ordinal: 0 }], 0, 0)).resolves.toBeNull();
    expect(calls).toHaveLength(0);
  });
});
