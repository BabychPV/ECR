import { apiEnqueue, apiFetch } from '@/api/client';
import { formatMonthYear } from '@/shared/format';
import type { components } from '@/api/schema';

/**
 * Склад газу (D-NEW, людина 24.11): потоки → кейси → версії складу, власник — ECR. Сервер —
 * `GasCompositionController` (`/api/v1/gas-composition/*`); правила шкали версій, Σ і замкнених періодів — там.
 */
export type GasStreamList = components['schemas']['GasStreamListDto'];
export type GasStreamListItem = components['schemas']['GasStreamListItemDto'];
export type GasStreamDetail = components['schemas']['GasStreamDetailDto'];
export type GasCase = components['schemas']['GasCaseDto'];
export type GasVersion = components['schemas']['GasVersionDto'];
export type GasComponent = components['schemas']['GasComponentDto'];
export type GasComponentValue = components['schemas']['GasComponentValue'];
export type GasChangeResult = components['schemas']['GasChangeResultDto'];
export type GasStreamRequest = components['schemas']['GasStreamRequest'];
export type GasVersionCreateRequest = components['schemas']['GasVersionCreateRequest'];
export type GasVersionUpdateRequest = components['schemas']['GasVersionUpdateRequest'];
export type GasImportRun = components['schemas']['FlertSyncRunDto'];

const Base = '/api/v1/gas-composition';

const json = (method: string, body: unknown): RequestInit => ({
  method,
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(body),
});

export const listStreams = (): Promise<GasStreamList> => apiFetch<GasStreamList>(`${Base}/streams`);

export const listComponents = (): Promise<GasComponent[]> => apiFetch<GasComponent[]>(`${Base}/components`);

export const getStream = (id: number): Promise<GasStreamDetail> =>
  apiFetch<GasStreamDetail>(`${Base}/streams/${String(id)}`);

export const createStream = (request: GasStreamRequest): Promise<GasChangeResult> =>
  apiFetch<GasChangeResult>(`${Base}/streams`, json('POST', request));

export const updateStream = (id: number, request: GasStreamRequest): Promise<GasChangeResult> =>
  apiFetch<GasChangeResult>(`${Base}/streams/${String(id)}`, json('PUT', request));

export const deleteStream = (id: number): Promise<GasChangeResult> =>
  apiFetch<GasChangeResult>(`${Base}/streams/${String(id)}`, { method: 'DELETE' });

export const createVersion = (streamId: number, request: GasVersionCreateRequest): Promise<GasChangeResult> =>
  apiFetch<GasChangeResult>(`${Base}/streams/${String(streamId)}/versions`, json('POST', request));

export const renameCase = (streamId: number, from: string, to: string): Promise<GasChangeResult> =>
  apiFetch<GasChangeResult>(`${Base}/streams/${String(streamId)}/cases`, json('PUT', { from, to }));

export const updateVersion = (versionId: number, request: GasVersionUpdateRequest): Promise<GasChangeResult> =>
  apiFetch<GasChangeResult>(`${Base}/versions/${String(versionId)}`, json('PUT', request));

export const deleteVersion = (versionId: number): Promise<GasChangeResult> =>
  apiFetch<GasChangeResult>(`${Base}/versions/${String(versionId)}`, { method: 'DELETE' });

/** Разовий імпорт із FLERT (лише в порожню базу): 202 з `jobId`. */
export const startImport = (): Promise<{ jobId: string }> => apiEnqueue(`${Base}/import`);

/**
 * Σ мол.% для показу (індикатор, «Σ = 99.98 %»), з точністю до 1e-9. ⚠ Самі значення на дроті — рядки `decimal`
 * повної точності (`DecimalAsStringJsonConverter`); їх клієнт шле назад як є, а Σ, що вирішує, рахує сервер.
 */
export function sumOf(values: readonly { molPct: string | number }[]): number {
  return Math.round(values.reduce((total, value) => total + Number(value.molPct), 0) * 1e9) / 1e9;
}

/** Допуск Σ навколо 100 (той самий, що на сервері — `GasCompositionRules.SumTolerance`). */
export const SumTolerance = 0.5;

/** Чи Σ поза 100 ± 0.5 (порожній склад — теж). */
export function deviates(sum: number): boolean {
  return sum === 0 || Math.abs(sum - 100) > SumTolerance + 1e-12;
}

/** Місяць дії версії мовою інтерфейсу: `2026-03-01` → «March 2026». */
export function monthOf(date: string): string {
  const [year, month] = date.split('-').map(Number);
  return formatMonthYear(year ?? 0, month ?? 0);
}

/**
 * Останній місяць, коли версія ще діє: кінець вікна виключний (`validTo` — перший день наступної версії), тож показуємо
 * місяць ДО нього: `2026-03-01` → «February 2026».
 */
export function lastMonthOf(validTo: string): string {
  const [year, month] = validTo.split('-').map(Number);
  const y = year ?? 0;
  const m = month ?? 1;
  return m === 1 ? formatMonthYear(y - 1, 12) : formatMonthYear(y, m - 1);
}