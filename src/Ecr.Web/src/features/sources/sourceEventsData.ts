import { apiFetch, EcrApiError } from '@/api/client';
import type {
  DocumentPage,
  DocumentSummary,
  DocumentTableDto,
  RegistryDefDto,
  RegistryEntryDto,
  TableSliceDto,
  UnitRef,
} from '@/api/types';
import type { MapColumn } from './sourceEventMapForm';

/**
 * Допоміжні читання вкладки «Події з PI» і форми мапінгу подій: документи, динамічні таблиці документа, їхні
 * колонки, одиниці й записи довідників — усе чинними ендпоінтами, без нового контракту.
 *
 * ⚠ Ключі кешу — власні, з префіксом `source-events`, а не з `queryKeys.ts` (спільний файл, зміни в ньому йдуть
 * через попередження). Ключі одиниць і довідників збігаються з рештою клієнта за формою запиту, але не за іменем —
 * подвійне читання тут дешевше, ніж правка спільного файлу.
 */
export const SourceEventsKeys = {
  all: ['source-events'] as const,
  events: (sourceEntityId: number, filters: unknown) => ['source-events', 'events', sourceEntityId, filters] as const,
  /** Префікс усіх сторінок і фільтрів подій сутності — для інвалідації після синку й правки мапінгу. */
  eventsOf: (sourceEntityId: number) => ['source-events', 'events', sourceEntityId] as const,
  maps: (sourceEntityId: number) =>['source-events', 'maps', sourceEntityId] as const,
  templates: (dataSourceId: number) => ['source-events', 'templates', dataSourceId] as const,
  documents: ['source-events', 'documents'] as const,
  tables: (documentId: number) => ['source-events', 'tables', documentId] as const,
  columns: (documentId: number, tableInstanceId: number) => ['source-events', 'columns', documentId, tableInstanceId] as const,
  units: ['source-events', 'units'] as const,
  registries: ['source-events', 'registries'] as const,
  entries: (code: string) => ['source-events', 'entries', code] as const,
};

/**
 * Документи, які користувач бачить (одна сторінка до 500): мапінг подій прив'язується до документа ділянки, а їх
 * у проєкті одиниці-десятки. Більше за сторінку — видно лише перші 500, і це названо в описі гілки.
 */
export async function fetchDocuments(): Promise<DocumentSummary[]> {
  const page = await apiFetch<DocumentPage>('/api/v1/documents?limit=500');

  return page.items;
}

/**
 * Динамічні таблиці документа — по одній на визначення таблиці (події лягають у визначення, а екземпляр
 * потрібен лише, щоб прочитати колонки).
 */
export async function fetchDynamicTables(documentId: number): Promise<DocumentTableDto[]> {
  const tables = await apiFetch<DocumentTableDto[]>(`/api/v1/documents/${documentId}/tables`);
  const seen = new Set<number>();

  return tables.filter((table) => {
    if (!table.allowsDynamicRows || seen.has(table.tableDefId)) return false;
    seen.add(table.tableDefId);
    return true;
  });
}

/** Колонки таблиці з одного її екземпляра (`ColumnDto` несе тип, довідник і одиницю колонки). */
export async function fetchTableColumns(documentId: number, tableInstanceId: number): Promise<MapColumn[]> {
  const slice = await apiFetch<TableSliceDto>(`/api/v1/documents/${documentId}/tables/${tableInstanceId}`);

  return slice.columns.map((column) => ({
    id: column.id,
    code: column.code,
    header: column.header,
    dataType: column.dataType,
    lookupRegistryDefId: column.lookupRegistryDefId,
    unitId: column.unitId,
  }));
}

export function fetchUnits(): Promise<UnitRef[]> {
  return apiFetch<UnitRef[]>('/api/v1/units');
}

export function fetchRegistries(): Promise<RegistryDefDto[]> {
  return apiFetch<RegistryDefDto[]>('/api/v1/registries');
}

export function fetchRegistryEntries(code: string): Promise<RegistryEntryDto[]> {
  return apiFetch<RegistryEntryDto[]>(`/api/v1/registries/${encodeURIComponent(code)}/entries`);
}

/** Адреса документа на потрібному періоді — та сама форма, що в переліку документів. */
export function documentHref(documentId: number, periodKey: number | null): string {
  return `/documents/${documentId}` + (periodKey === null ? '' : `?periodKey=${periodKey}`);
}

/**
 * Каталог шаблонів не налаштовано на середовищі — стан, а не порожній список (§4.7.2): `422 ECR-INT-0422`
 * (`eventQueryNotConfigured`, `queryKindNotConfigured`, `queryKindNotSupported`).
 */
export function isEventsNotConfigured(error: unknown): boolean {
  return error instanceof EcrApiError && error.problem.status === 422 && error.problem.errorCode === 'ECR-INT-0422';
}
