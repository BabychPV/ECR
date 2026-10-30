import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  analyseAirImport,
  applyAirImport,
  getAirLimits,
  getImportProfile,
  getNaeTree,
  listImportProfiles,
  previewAirImport,
  saveImportProfile,
} from './airApi';
import type {
  AirAnalysis,
  AirImportOptions,
  AirImportResult,
  AirLimitsPermit,
  AirPreview,
  ImportProfile,
  ImportProfileSummary,
  NaeTree,
  SaveImportProfileRequest,
} from './airTypes';

/*
 * Ключі кешу — під `['permits', …]`: будь-яка дія над дозволом (`permitScope`) скидає й картку. Локальні, не в
 * `api/queryKeys.ts`: ключі ніхто поза `features/permits/air` не читає.
 */
export const airKeys = {
  card: (permitId: number) => ['permits', 'air', 'card', permitId] as const,
  profiles: ['permits', 'air', 'profiles'] as const,
  profile: (code: string) => ['permits', 'air', 'profile', code] as const,
  tree: ['permits', 'air', 'nae-tree'] as const,
};

/**
 * Картка дозволу. ⚠ `structuralSharing: false`: у дозволі сотні джерел і тисячі речовин; зіставлення нового зрізу зі
 * старим по вузлах коштує більше, ніж заміна цілком (урок `features/grid/sliceCache.ts`), а посилальна
 * стабільність частин тут нікому не потрібна.
 */
export function useAirLimits(permitId: number) {
  return useQuery<AirLimitsPermit>({
    queryKey: airKeys.card(permitId),
    queryFn: () => getAirLimits(permitId),
    enabled: Number.isInteger(permitId) && permitId > 0,
    structuralSharing: false,
  });
}

export function useImportProfiles() {
  return useQuery<ImportProfileSummary[]>({ queryKey: airKeys.profiles, queryFn: listImportProfiles });
}

export function useImportProfile(code: string | undefined) {
  return useQuery<ImportProfile>({
    queryKey: airKeys.profile(code ?? ''),
    queryFn: () => getImportProfile(code ?? ''),
    enabled: code !== undefined && code !== '',
  });
}

/** Дерево НАЕ для вибору вузла «створити під». Вантажиться, лише коли є що розв'язувати. */
export function useNaeTree(enabled: boolean) {
  return useQuery<NaeTree>({
    queryKey: airKeys.tree,
    queryFn: getNaeTree,
    enabled,
    structuralSharing: false,
  });
}

export interface AirImportCall {
  readonly file: File;
  readonly options: AirImportOptions;
}

/**
 * Розкладка файлу. ⚠ `handled`: відмову показує сам крок майстра (банер кроку), глобальна сітка тоста не кидає —
 * інакше одна відмова дала б два сповіщення (L9-01).
 */
export function useAnalyseAirImport() {
  return useMutation<AirAnalysis, Error, AirImportCall>({
    mutationFn: ({ file, options }) => analyseAirImport(file, options),
    meta: { handled: true },
  });
}

export function usePreviewAirImport(permitId: number) {
  return useMutation<AirPreview, Error, AirImportCall>({
    mutationFn: ({ file, options }) => previewAirImport(permitId, file, options),
    meta: { handled: true },
  });
}

/** Застосування; після успіху скидає весь піддомен `['permits']` (картка, шапка, лічильники рядків таблиць). */
export function useApplyAirImport(permitId: number) {
  const client = useQueryClient();

  return useMutation<AirImportResult, Error, AirImportCall>({
    mutationFn: ({ file, options }) => applyAirImport(permitId, file, options),
    meta: { handled: true },
    onSuccess: () => client.invalidateQueries({ queryKey: ['permits'] }),
  });
}

export function useSaveImportProfile() {
  const client = useQueryClient();

  return useMutation<ImportProfile, Error, { readonly code: string; readonly body: SaveImportProfileRequest }>({
    mutationFn: ({ code, body }) => saveImportProfile(code, body),
    meta: { handled: true },
    onSuccess: (saved) => {
      client.setQueryData(airKeys.profile(saved.code), saved);
      return client.invalidateQueries({ queryKey: airKeys.profiles });
    },
  });
}
