import { readFileSync } from 'node:fs';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { notifications } from '@mantine/notifications';
import type { TableSliceDto } from '@/api/types';
import { buildRequest, type PendingEdit } from '../cellPatchRequest';
import { planPaste } from '../clipboard';
import { PatchCellsMaxCells, saveInParts, splitIntoParts, type PartResult } from '../patchParts';
import { discardPendingRows, openDocument, putPendingEdits, resetPending } from '../pendingStore';

/**
 * PSS-17 (рішення людини №12): пакет понад стелю сервера ДІЛИТЬСЯ на послідовні частини ≤ 50 000 комірок.
 *
 * Мутаційні докази (не в коміті):
 *  - `splitIntoParts` повертає `[[...edits]]` завжди (поділу немає) → червоні «51 000», «120 000» і сторож стелі;
 *  - `planPaste` повертає `targets` і за наявності відмов (перевірки до поділу немає) → червоний «відмова в
 *    останній частині відхиляє всю вставку».
 */

vi.mock('@mantine/notifications', () => ({
  notifications: { show: vi.fn(), update: vi.fn(), hide: vi.fn() },
}));

const show = vi.mocked(notifications.show);
const update = vi.mocked(notifications.update);
const hide = vi.mocked(notifications.hide);

beforeEach(() => {
  show.mockClear();
  update.mockClear();
  hide.mockClear();
});

afterEach(() => {
  vi.restoreAllMocks();
});

/** `rows` × `columns` правок; рядок `r{n}`, колонка `C{n}`. */
function edits(rows: number, columns: number, version = 'v1'): PendingEdit[] {
  const result: PendingEdit[] = [];
  for (let r = 0; r < rows; r++) {
    for (let c = 0; c < columns; c++) {
      result.push({ rowKey: `r${String(r)}`, columnCode: `C${String(c)}`, value: r * columns + c, isEmpty: false, baseVersion: version });
    }
  }
  return result;
}

function cellsOf(request: { rows: readonly { cells: readonly unknown[] }[] }): number {
  return request.rows.reduce((sum, row) => sum + row.cells.length, 0);
}

/** Рядки, що потрапили в кілька частин (розірвані). */
function splitRows(parts: readonly (readonly PendingEdit[])[]): string[] {
  const seen = new Map<string, number>();
  parts.forEach((part, index) => {
    for (const edit of part) {
      const first = seen.get(edit.rowKey);
      if (first === undefined) seen.set(edit.rowKey, index);
      else if (first !== index) seen.set(edit.rowKey, -1);
    }
  });
  return [...seen].filter(([, index]) => index === -1).map(([rowKey]) => rowKey);
}

describe('PSS-17: стеля клієнта — та сама, що в сервера', () => {
  it('PatchCellsMaxCells дорівнює PatchCellsRequest.MaxCells', () => {
    const server = readFileSync(
      path.resolve(process.cwd(), '..', 'Ecr.Application', 'Documents', 'Dto', 'PatchCellsRequest.cs'),
      'utf8',
    );
    const declared = /public const int MaxCells = ([\d_]+);/.exec(server)?.[1];

    expect(declared).toBeDefined();
    expect(Number((declared ?? '').replace(/_/g, ''))).toBe(PatchCellsMaxCells);
  });
});

describe('PSS-17: splitIntoParts', () => {
  it('у межах стелі — одна частина з тими самими правками (запит як доти)', () => {
    const batch = edits(1000, 50);
    const parts = splitIntoParts(batch);

    expect(parts).toHaveLength(1);
    expect(parts[0]).toEqual(batch);
    expect(splitIntoParts([])).toEqual([]);
  });

  it('51 000 комірок → дві частини, кожна ≤ 50 000, рядки не розірвані, нічого не загублено', () => {
    const batch = edits(1020, 50);
    const parts = splitIntoParts(batch);

    expect(batch).toHaveLength(51_000);
    expect(parts.map((part) => part.length)).toEqual([50_000, 1_000]);
    expect(splitRows(parts)).toEqual([]);
    expect(new Set(parts.flat())).toEqual(new Set(batch));
  });

  it('120 000 комірок (60 колонок) → три частини по цілих рядках', () => {
    const batch = edits(2000, 60);
    const parts = splitIntoParts(batch);

    // 50 000 / 60 = 833 рядки (49 980 комірок) на частину.
    expect(parts.map((part) => part.length)).toEqual([49_980, 49_980, 20_040]);
    expect(splitRows(parts)).toEqual([]);
    expect(parts.flat()).toHaveLength(120_000);
  });

  it('сторож: жоден запит, зібраний із частин, не перевищує стелі сервера', () => {
    for (const batch of [edits(1020, 50), edits(2000, 60), edits(3001, 17)]) {
      const requests = splitIntoParts(batch).map((part) => buildRequest(1, 202609, part));

      expect(requests.length).toBeGreaterThan(1);
      for (const request of requests) expect(cellsOf(request)).toBeLessThanOrEqual(PatchCellsMaxCells);
      expect(requests.reduce((sum, request) => sum + cellsOf(request), 0)).toBe(batch.length);
    }
  });

  it('рядок, більший за стелю, ріжеться порціями, решта рядків — цілими', () => {
    const wide = edits(1, 7).map((edit) => ({ ...edit, rowKey: 'wide' }));
    const narrow = edits(2, 2);
    const parts = splitIntoParts([...narrow.slice(0, 2), ...wide, ...narrow.slice(2)], 3);

    expect(parts.map((part) => part.map((edit) => `${edit.rowKey}:${edit.columnCode}`))).toEqual([
      ['r0:C0', 'r0:C1', 'wide:C0'],
      ['wide:C1', 'wide:C2', 'wide:C3'],
      ['wide:C4', 'wide:C5', 'wide:C6'],
      ['r1:C0', 'r1:C1'],
    ]);
  });
});

describe('PSS-17: перевірка всієї вставки — до першої частини', () => {
  const allowAll = (): string | null => null;

  function matrix(rows: number, columns: number): string[][] {
    return Array.from({ length: rows }, (_, r) => Array.from({ length: columns }, (_, c) => String(r * columns + c)));
  }

  it('120 000 цілей: план цілий, а поділ дає частини в межах стелі', () => {
    const rowKeys = Array.from({ length: 2000 }, (_, i) => `r${String(i)}`);
    const columns = Array.from({ length: 60 }, (_, i) => `C${String(i)}`);
    const plan = planPaste(matrix(2000, 60), rowKeys, columns, { rowIndex: 0, columnIndex: 0 }, allowAll, () => false);

    expect(plan.targets).toHaveLength(120_000);

    const planned: PendingEdit[] = plan.targets.map((target) => ({ ...target, isEmpty: false, baseVersion: 'v1' }));
    const parts = splitIntoParts(planned);
    expect(parts).toHaveLength(3);
    for (const part of parts) expect(part.length).toBeLessThanOrEqual(PatchCellsMaxCells);
  });

  it('51 000 цілей: відмова в комірці, що лягла б в ОСТАННЮ частину, відхиляє всю вставку — не надсилається нічого', () => {
    const rowKeys = Array.from({ length: 1020 }, (_, i) => `r${String(i)}`);
    const columns = Array.from({ length: 50 }, (_, i) => `C${String(i)}`);
    const lastCell = (rowKey: string, columnCode: string): string | null =>
      rowKey === 'r1019' && columnCode === 'C49' ? 'рахує система' : null;

    const plan = planPaste(matrix(1020, 50), rowKeys, columns, { rowIndex: 0, columnIndex: 0 }, lastCell, () => false);

    expect(plan.rejected).toEqual([{ rowKey: 'r1019', columnCode: 'C49', reason: 'рахує система', kind: 'guard' }]);
    expect(plan.targets).toEqual([]);
    expect(splitIntoParts(plan.targets.map((target) => ({ ...target, isEmpty: false, baseVersion: 'v1' })))).toEqual([]);
  });
});

/** Кеш зрізу з версіями рядків — як його лишає `applyPatchLocally`. */
function sliceWith(versions: Record<string, string>): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId: 1,
    columns: [],
    rows: Object.entries(versions).map(([rowKey, rowVersion], ordinal) => ({
      cells: {},
      isOrphaned: false,
      label: null,
      ordinal,
      rowKey,
      rowKind: 'Item',
      rowVersion,
    })),
  } as TableSliceDto;
}


const Table = 1;
const Period = 202609;

/** Текст тоста: рядок або `<span role="status">текст</span>` (хід — у ввічливій області). */
function textOf(message: unknown): string {
  if (typeof message === 'string') return message;
  const element = message as { props?: { children?: unknown } } | null;
  return String(element?.props?.children ?? '');
}

function roleOf(message: unknown): string | undefined {
  return (message as { props?: { role?: string } } | null)?.props?.role;
}

/** Правки лежать у сховищі — як завжди перед збереженням (`saveThroughStore`, автозбереження). */
function stored(batch: PendingEdit[]): PendingEdit[] {
  putPendingEdits(Table, Period, batch);
  return batch;
}

const options = { tableInstanceId: Table, periodKey: Period, slice: () => undefined };
const NoticeId = `ecr-patch-parts-partial-${String(Table)}:${String(Period)}`;

describe('PSS-17: saveInParts', () => {
  beforeEach(() => {
    resetPending();
    openDocument(42);
  });

  afterEach(() => {
    resetPending();
  });

  it('частини йдуть ПОСЛІДОВНО: наступна — лише після відповіді попередньої; хід — у ввічливій області', async () => {
    const batch = stored(edits(2000, 60));
    const started: number[] = [];
    let release: (() => void) | null = null;

    const done = saveInParts(
      batch,
      (part) => {
        started.push(part.length);
        return new Promise<PartResult>((resolve) => {
          release = () => resolve('saved');
        });
      },
      options,
    );

    await Promise.resolve();
    expect(started).toEqual([49_980]);
    // Поки перша не відповіла — друга не почалася.
    await new Promise((resolve) => setTimeout(resolve, 10));
    expect(started).toEqual([49_980]);

    (release as unknown as () => void)();
    await vi.waitFor(() => expect(started).toHaveLength(2));
    (release as unknown as () => void)();
    await vi.waitFor(() => expect(started).toHaveLength(3));
    (release as unknown as () => void)();

    await expect(done).resolves.toEqual({
      total: 120_000, saved: 120_000, superseded: 0, parts: 3, failedPart: null, stoppedBy: null,
    });

    expect(show).toHaveBeenCalledTimes(1);
    expect(textOf(show.mock.calls[0]?.[0].message)).toContain('grid.saveParts.progress');
    // L3: оновлення ліку — `role="status"` (ввічливо), а не лише `alert` кореня тоста.
    expect(roleOf(show.mock.calls[0]?.[0].message)).toBe('status');
    const messages = update.mock.calls.map(([data]) => textOf(data.message));
    expect(messages.filter((message) => message.includes('grid.saveParts.progress'))).toHaveLength(3);
    expect(messages.at(-1)).toContain('grid.saveParts.done');
  });

  it('відмова частини 2: частина 3 не надсилається; тост зрізу — «збережено X з Y, решта НЕ збережена»', async () => {
    const batch = stored(edits(2000, 60));
    const sent: number[] = [];

    const outcome = await saveInParts(
      batch,
      (part) => {
        sent.push(part.length);
        return Promise.resolve(sent.length === 2 ? 'refused' : 'saved');
      },
      options,
    );

    expect(sent).toEqual([49_980, 49_980]);
    expect(outcome).toEqual({ total: 120_000, saved: 49_980, superseded: 0, parts: 3, failedPart: 1, stoppedBy: 'refused' });

    const partial = show.mock.calls.at(-1)?.[0];
    expect(textOf(partial?.message)).toContain('grid.saveParts.partial');
    expect(partial?.id).toBe(NoticeId);
    expect(partial?.color).toBe('statusWarning');
    expect(partial?.autoClose).toBe(false);
  });

  it('M1: тост «не збережено» оновлює лік і ГАСНЕ, щойно решту довезено (Retry, автозбереження)', async () => {
    const batch = stored(edits(2000, 60));
    let calls = 0;

    // Як справжній зберігач: успіх частини знімає її рядки зі сховища (`discardPendingRows`).
    await saveInParts(
      batch,
      (part) => {
        calls += 1;
        if (calls === 2) return Promise.resolve('refused');
        discardPendingRows(Table, Period, new Set(part.map((edit) => edit.rowKey)));
        return Promise.resolve('saved');
      },
      options,
    );

    hide.mockClear();
    update.mockClear();

    // Retry довіз другу частину — лік оновився, тост іще живий.
    discardPendingRows(Table, Period, new Set(batch.slice(49_980, 99_960).map((edit) => edit.rowKey)));
    const counted = update.mock.calls.filter(([data]) => data.id === NoticeId).map(([data]) => textOf(data.message));
    expect(counted.at(-1)).toContain('grid.saveParts.stillUnsaved');
    expect(counted.at(-1)).toContain('20');
    expect(hide).not.toHaveBeenCalledWith(NoticeId);

    // Решту довезено — тост гасне сам.
    discardPendingRows(Table, Period, new Set(batch.slice(99_960).map((edit) => edit.rowKey)));
    expect(hide).toHaveBeenCalledWith(NoticeId);
  });

  it('M1: наступний пакет частинами в тому самому зрізі прибирає старий тост — двох тостів із різними числами немає', async () => {
    const batch = stored(edits(1020, 50));
    let calls = 0;
    await saveInParts(batch, () => Promise.resolve((calls += 1) === 2 ? 'refused' : 'saved'), options);
    expect(show.mock.calls.some(([data]) => data.id === NoticeId)).toBe(true);
    hide.mockClear();

    await saveInParts(batch, () => Promise.resolve('saved'), options);

    expect(hide).toHaveBeenCalledWith(NoticeId);
  });

  it('M1: «дані зайняті» з повтором — текст «чекає, повториться саме», а не «див. помилку» (її немає)', async () => {
    const batch = stored(edits(1020, 50));
    let calls = 0;
    const outcome = await saveInParts(batch, () => Promise.resolve((calls += 1) === 2 ? 'waiting' : 'saved'), options);

    expect(outcome.stoppedBy).toBe('waiting');
    const partial = show.mock.calls.at(-1)?.[0];
    expect(textOf(partial?.message)).toContain('grid.saveParts.waiting');
    expect(textOf(partial?.message)).not.toContain('grid.saveParts.partial');
  });

  it('відмова ПЕРШОЇ частини: нічого не збережено — лічильний тост прибирається, тоста зрізу немає', async () => {
    const outcome = await saveInParts(stored(edits(1020, 50)), () => Promise.resolve('refused'), options);

    expect(outcome).toEqual({ total: 51_000, saved: 0, superseded: 0, parts: 2, failedPart: 0, stoppedBy: 'refused' });
    expect(hide).toHaveBeenCalledTimes(1);
    expect(show).toHaveBeenCalledTimes(1);
  });

  it('пакет в одну частину — без тоста ходу', async () => {
    await saveInParts(stored(edits(10, 10)), () => Promise.resolve('saved'), options);

    expect(show).not.toHaveBeenCalled();
    expect(update).not.toHaveBeenCalled();
  });

  it('M2: комірка, змінена чи скасована, доки йшла частина 1, у частині 2 НЕ їде; лік чесний', async () => {
    const batch = stored(edits(1020, 50));
    const sentParts: PendingEdit[][] = [];

    const outcome = await saveInParts(
      batch,
      (part) => {
        sentParts.push(part);
        if (sentParts.length === 1) {
          // Поки летить частина 1: людина ввела нове значення в r1000:C0 і скасувала r1001:C0 (Undo — значення «було»).
          putPendingEdits(Table, Period, [
            { rowKey: 'r1000', columnCode: 'C0', value: 'typed', isEmpty: false, baseVersion: 'v1' },
            { rowKey: 'r1001', columnCode: 'C0', value: 0, isEmpty: false, baseVersion: 'v1' },
          ]);
        }
        return Promise.resolve('saved');
      },
      options,
    );

    const second = sentParts[1] ?? [];
    expect(second).toHaveLength(998);
    expect(second.some((edit) => edit.rowKey === 'r1000' && edit.columnCode === 'C0')).toBe(false);
    expect(second.some((edit) => edit.rowKey === 'r1001' && edit.columnCode === 'C0')).toBe(false);
    expect(outcome).toMatchObject({ saved: 50_998, superseded: 2, failedPart: null });
  });

  it('L2: документ закрито посеред пакета — решта не їде, людині сказано прямо', async () => {
    const batch = stored(edits(2000, 60));
    let calls = 0;

    const outcome = await saveInParts(
      batch,
      () => {
        calls += 1;
        // Людина вийшла з документа (діалог виходу → «піти»): сховище скинуто.
        resetPending();
        return Promise.resolve('saved');
      },
      options,
    );

    expect(calls).toBe(1);
    expect(outcome).toMatchObject({ saved: 49_980, failedPart: 1, stoppedBy: 'left' });
    expect(show.mock.calls.map(([data]) => textOf(data.message)).at(-1)).toContain('grid.saveParts.stopped');
  });

  it('наступна частина бере версію рядка з кешу після попередньої; «Keep mine» для вже надісланого рядка застарів', async () => {
    // Рядок `wide` (5 комірок) розірвано між частинами по 3; рядок `b` іде лише в другій частині.
    const wide = edits(1, 5).map((edit) => ({ ...edit, rowKey: 'wide' }));
    const b: PendingEdit = { rowKey: 'b', columnCode: 'C0', value: 1, isEmpty: false, baseVersion: 'b1' };
    stored([...wide, b]);
    let cache = sliceWith({ wide: 'w1', b: 'b1' });
    const versionsSent: Record<string, string | null>[] = [];

    await saveInParts(
      [...wide, b],
      (part) => {
        versionsSent.push(Object.fromEntries(part.map((edit) => [edit.rowKey, edit.baseVersion])));
        // Відповідь сервера підняла версію надісланих рядків — так кеш оновлює `applyPatchLocally`.
        cache = sliceWith({ wide: 'w2', b: cache.rows.find((row) => row.rowKey === 'b')?.rowVersion ?? 'b1' });
        return Promise.resolve('saved');
      },
      { ...options, slice: () => cache, versionOverrides: new Map([['wide', 'w-keep'], ['b', 'b-keep']]), max: 3 },
    );

    // Перша частина — як її віддав зберігач (версії вже з «Keep mine»/кешу); далі: `wide` — з відповіді
    // (не застарілий «Keep mine»), `b` — ще не надсилався, тож «Keep mine» для нього чинний.
    expect(versionsSent).toEqual([{ wide: 'v1' }, { wide: 'w2', b: 'b-keep' }]);
  });
});
