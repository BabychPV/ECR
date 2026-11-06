import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { saveBatch, type RegistryBatchResult } from '@/features/registries/rows/api';
import {
  analyseAirImport,
  applyAirImport,
  getAirLimits,
  getAirScopeCodes,
  getAirScopeFields,
  getImportProfile,
  getNaeTree,
  listImportProfiles,
  previewAirImport,
  saveImportProfile,
  type AirScopeForm,
} from './airApi';
import type {
  AirAnalysis,
  AirImportOptions,
  AirImportResult,
  AirLimitsPermit,
  AirPreview,
  AirScopeCodes,
  AirScopeFields,
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
  scope: ['permits', 'air', 'scope-codes'] as const,
  scopeFields: ['permits', 'air', 'scope-fields'] as const,
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
  /** Поля області імпорту (лише preview/apply). */
  readonly scope?: AirScopeForm;
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
    mutationFn: ({ file, options, scope }) => previewAirImport(permitId, file, options, scope),
    meta: { handled: true },
  });
}

/** Застосування; після успіху скидає весь піддомен `['permits']` (картка, шапка, лічильники рядків таблиць). */
export function useApplyAirImport(permitId: number) {
  const client = useQueryClient();

  return useMutation<AirImportResult, Error, AirImportCall>({
    mutationFn: ({ file, options, scope }) => applyAirImport(permitId, file, options, scope),
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

/** Запис одного рядка картки (джерело чи речовина) загальним пакетним записом довідника. */
export interface AirRowSave {
  /** Довідник таблиці (`sourcesRegistryCode` / `substancesRegistryCode` картки). */
  readonly registryCode: string;
  readonly id: number;
  /** `version` рядка з картки — `baseVersion` пакета; застарілий дає помилку рядка `entryChanged`. */
  readonly version: string | null;
  readonly values: Readonly<Record<string, string | number | null>>;
}

/**
 * P1 07.11: правка рядка на картці — тим самим `POST /registries/{code}/entries/batch`, що й сітка таблиці дозволу (нового
 * шляху запису немає). Звіт завжди 200: `applied: false` — нічого не записано, причина в `rows[].errors`. Після будь-якого
 * результату картка перечитується (свіжа версія рядка; після `entryChanged` — чужі значення).
 */
export function useSaveAirRow(permitId: number) {
  const client = useQueryClient();

  return useMutation<RegistryBatchResult, Error, AirRowSave>({
    mutationFn: ({ registryCode, id, version, values }) =>
      saveBatch(registryCode, [{ clientRowId: `air-${String(id)}`, op: 'upsert', id, code: null, baseVersion: version, values: { ...values } }], false),
    meta: { handled: true },
    onSettled: () => client.invalidateQueries({ queryKey: airKeys.card(permitId) }),
  });
}

/** Regions and areas of the NAE tree: which Region has areas (scope step). */
export function useAirScopeCodes() {
  return useQuery<AirScopeCodes>({ queryKey: airKeys.scope, queryFn: getAirScopeCodes });
}

/** AIR-ON-AIR: how REGION / AREA are configured on the AIR type, with their values (scope step). */
export function useAirScopeFields() {
  return useQuery<AirScopeFields>({ queryKey: airKeys.scopeFields, queryFn: getAirScopeFields });
}
