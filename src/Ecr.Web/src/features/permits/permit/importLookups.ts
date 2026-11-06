import type { ImportMapping, LookupResult } from './importApi';
import { pickerResolver, type DerivedHit, type LookupHit, type LookupResolver } from './importRows';
import { isLookup, type PermitField, type PickerData } from './model';

/*
 * Значення стовпців-посилань вставки → записи (людина 06.11: NAE, Region, Area з різних реєстрів). Спільні реєстри й списки типу
 * розв'язує СЕРВЕР за кодом або назвою будь-якою мовою (`POST …/import/lookups`); таблицю того самого дозволу
 * (`SamePermitTable`) - пікер, як і раніше: її рядки належать дозволу, а не реєстру.
 */

/** Стовпець розв'язує сервер: посилання на спільний реєстр або список типу. */
export function resolvedOnServer(field: PermitField): boolean {
  return isLookup(field.column) && field.source.listScope !== 'SamePermitTable';
}

/** Різні непорожні значення кожного серверного стовпця-посилання (у порядку стовпців таблиці). */
export function lookupRequest(
  lines: readonly (readonly string[])[],
  fields: readonly PermitField[],
): { column: string; values: string[] }[] {
  return fields.flatMap((field, index) => {
    if (!resolvedOnServer(field)) return [];
    const values = [...new Set(lines.map((line) => (line[index] ?? '').trim()).filter((value) => value !== ''))].sort();
    return values.length === 0 ? [] : [{ column: field.column.code, values }];
  });
}

const ServerReason: Readonly<Record<string, 'unknownValue' | 'ambiguous' | 'notSource'>> = {
  unknown: 'unknownValue',
  ambiguous: 'ambiguous',
  notSource: 'notSource',
};

/**
 * Резолвер плану: серверні стовпці - з відповіді `result` (`undefined` - ще не прийшла: значення «у перевірці»), решта - пікери.
 * `derive` - Region/Area джерела НАЕ з тієї ж відповіді; `derivable`/`naeColumns` - зі збереженого зіставлення.
 */
export function serverResolver(
  fields: readonly PermitField[],
  result: LookupResult | undefined,
  mapping: ImportMapping | undefined,
  picker: PickerData,
): LookupResolver {
  const local = pickerResolver(fields, picker);
  const byColumn = new Map<string, Map<string, LookupHit>>();
  for (const column of result?.columns ?? []) {
    byColumn.set(
      column.column,
      new Map(
        column.values.map((value): [string, LookupHit] => [
          value.text.trim(),
          value.entryId == null
            ? { reason: ServerReason[value.reason ?? 'unknown'] ?? 'unknownValue' }
            : { id: String(value.entryId), label: value.label ?? String(value.entryId) },
        ]),
      ),
    );
  }

  const derived = new Map<string, Map<string, DerivedHit>>();
  for (const source of result?.derived ?? []) {
    derived.set(
      String(source.naeEntryId),
      new Map(
        source.cells.map((cell): [string, DerivedHit] => [
          cell.column,
          cell.entryId == null ? { reason: cell.reason ?? 'noMatch' } : { id: String(cell.entryId), label: cell.label ?? '' },
        ]),
      ),
    );
  }

  return {
    resolve: (field, text) => {
      if (!resolvedOnServer(field)) return local.resolve(field, text);
      if (result === undefined) return null;
      // Стовпця немає у відповіді - сервер його не знає (реєстру немає): значення не розв'язане. Значення поза відповіддю -
      // ще не перевірене (запит із новим набором у дорозі).
      const column = byColumn.get(field.column.code);
      if (column === undefined) return { reason: 'unknownValue' };
      return column.get(text.trim()) ?? null;
    },
    derive: (naeId) => derived.get(naeId),
    derivable: mapping?.derivable ?? [],
    naeColumns: mapping?.naeColumns ?? [],
  };
}
