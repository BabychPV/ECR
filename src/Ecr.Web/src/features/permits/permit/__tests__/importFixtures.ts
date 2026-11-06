import { LimitsRegistry } from './permitFixtures';

/*
 * Сервер вставки/імпорту для тестів екрана дозволу (людина 06.11): збережене зіставлення стовпців і розв'язання значень
 * посилань за кодом чи назвою (`POST …/import/lookups`). Дані - з `rowsFor` (`permitFixtures.ts`).
 */

/** Записи спільного довідника й списку типу таблиці Limits: id, код, назва. */
const Entries: Readonly<Record<string, readonly { id: number; code: string; name: string }[]>> = {
  Use: [
    { id: 21, code: 'COOL', name: 'Cooling' },
    { id: 22, code: 'PROC', name: 'Process' },
  ],
  Substance: [
    { id: 41, code: 'CH4', name: 'Methane' },
    { id: 42, code: 'C6H6', name: 'Benzene' },
    { id: 43, code: 'X1', name: 'Xylene' },
    { id: 44, code: 'X2', name: 'Xylene' },
  ],
};

export interface FakeImportServer {
  /** Збережене зіставлення (`columns`) і його токен. */
  mapping: { columns: Record<string, string>; rowVersion: string | null; derivable: string[]; naeColumns: string[] };
  /** Тіла `PUT …/import-mapping` з `If-Match`. */
  readonly saves: { columns: Record<string, string>; ifMatch: string | null }[];
  /** Тіла `POST …/import/lookups`. */
  readonly lookups: unknown[];
  /** Аркуші книги `.xlsx`: назва → рядки (перший - заголовок). */
  workbook: Record<string, string[][]>;
  /** Які аркуші просив діалог (`null` - перший видимий). */
  readonly sheetsRead: (string | null)[];
}

export function fakeImportServer(): FakeImportServer {
  return { mapping: { columns: {}, rowVersion: null, derivable: [], naeColumns: [] }, saves: [], lookups: [], workbook: {}, sheetsRead: [] };
}

const key = (text: string): string => text.trim().replace(/\s+/g, ' ').toUpperCase();

/** Відповідь фейкового сервера на адресу вставки/імпорту; `undefined` - адреса не його. */
export function importResponse(server: FakeImportServer, path: string, init?: RequestInit): unknown {
  const base = `/api/v1/permit-tables/${LimitsRegistry}`;
  if (path === `${base}/import-mapping`) {
    if (init?.method === 'PUT') {
      const body = JSON.parse(String(init.body)) as { columns: Record<string, string> };
      const headers = (init.headers ?? {}) as Record<string, string>;
      server.saves.push({ columns: body.columns, ifMatch: headers['If-Match'] ?? null });
      server.mapping = { ...server.mapping, columns: body.columns, rowVersion: `rv${String(server.saves.length)}` };
    }
    return { registryCode: LimitsRegistry, updatedAt: null, ...server.mapping };
  }
  if (path === `${base}/import/workbook`) {
    const form = init?.body as FormData;
    const sheet = form.get('sheet');
    const chosen = typeof sheet === 'string' ? sheet : (Object.keys(server.workbook)[0] ?? '');
    server.sheetsRead.push(typeof sheet === 'string' ? sheet : null);
    return { sheet: chosen, sheets: Object.keys(server.workbook), rows: server.workbook[chosen] ?? [], truncated: false };
  }
  if (path === `${base}/import/lookups`) {
    const body = JSON.parse(String(init?.body)) as { columns: { column: string; values: string[] }[] };
    server.lookups.push(body);
    return {
      columns: body.columns.map(({ column, values }) => ({
        column,
        registryCode: column,
        values: values.map((text) => {
          const entries = Entries[column] ?? [];
          const byCode = entries.filter((entry) => key(entry.code) === key(text));
          const found = byCode.length === 1 ? byCode : entries.filter((entry) => key(entry.name) === key(text));
          const one = found.length === 1 ? found[0] : undefined;
          return one === undefined
            ? { text, entryId: null, label: null, reason: found.length > 1 ? 'ambiguous' : 'unknown', matches: found.length }
            : { text, entryId: one.id, label: one.name, reason: null, matches: 1 };
        }),
      })),
      derived: [],
    };
  }
  return undefined;
}
