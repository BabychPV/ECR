import { expect, type APIRequestContext, type APIResponse } from '@playwright/test';

/**
 * AIR-ON-AIR e2e fixture: the import scope (REGION / AREA columns of the AIR type, `GET /air-limits/scope-fields`). By default both
 * are Registry columns: REGION → `LAND_REGION` (Land bootstrap, absent on a fresh stand), AREA → `AIR_AREA`. A missing registry is
 * created through the ordinary registries API (it stays: the API cannot delete a registry; the stand's database is temporary); the
 * entries the spec adds are removed by `removeScopeEntries`.
 */
export const RegionRegistry = 'LAND_REGION';
export const AreaRegistry = 'AIR_AREA';

export interface ScopeOption {
  readonly entryId: number | null;
  readonly code: string;
  readonly name: string;
  readonly naeRegion: string | null;
  readonly naeArea: string | null;
}
export interface ScopeField {
  readonly columnCode: string;
  readonly kind: string;
  readonly registryCode: string | null;
  readonly required: boolean;
  readonly registryFound: boolean;
  readonly options: readonly ScopeOption[];
}
export interface ScopeFields {
  readonly region: ScopeField;
  readonly area: ScopeField;
}

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

const NaeTree = 'NAE_TREE';
const Substances = 'LAND_AIR_SUBSTANCE';

function name(code: string): { values: Record<string, string> } {
  return { values: { en: code } };
}

/** Реєстр дерева НАЕ - як `NaeTreeProvisioner` (поля, складений ключ, п'ять рівнів), якщо його немає. */
export async function ensureNaeTree(api: APIRequestContext): Promise<number> {
  const existing = await api.get(`/api/v1/registries/${NaeTree}/definition`);
  if (existing.status() === 404) {
    await ok(await api.post('/api/v1/registries', { data: { code: NaeTree, nameL10n: { en: 'NAE structure' }, isTemporal: true } }), 'створення NAE_TREE');
    const fields = ['NaeCode', 'RegionCode', 'AreaCode', 'AfPath', 'SourceType', 'PointName'].map((code, index) => ({
      id: null, code, nameL10n: name(code), dataType: 'String', ordinal: index + 1, isRequired: false, isKey: index === 0,
      lookupRegistryDefId: null, unitId: null,
    }));
    await ok(
      await api.put(`/api/v1/registries/${NaeTree}/definition`, {
        headers: { 'If-Match': '1' },
        data: {
          fields, rules: [], reason: 'e2e fixture: NAE tree',
          keys: [{ id: null, code: 'NAE_BUSINESS', nameL10n: name('NAE business key'), fieldCodes: ['NaeCode', 'RegionCode', 'AreaCode'], isPrimary: false, ignoreCase: true, isActive: true }],
        },
      }),
      'опис NAE_TREE',
    );
    const levels = [['REGION', false], ['AREA', true], ['SITE', false], ['EQUIP', true], ['SRC', false]] as const;
    await ok(
      await api.put(`/api/v1/registries/${NaeTree}/hierarchy`, {
        data: { isHierarchical: true, levels: levels.map(([code, isOptional]) => ({ code, name: name(code), isOptional })) },
      }),
      'рівні NAE_TREE',
    );
  }

  return (await ok<{ id: number }>(await api.get(`/api/v1/registries/${NaeTree}/definition`), 'NAE_TREE')).id;
}

/** Довідник речовин (поле SUBSTANCE_CODE, записи E<код>), якщо його немає. */
export async function ensureSubstances(api: APIRequestContext): Promise<void> {
  if ((await api.get(`/api/v1/registries/${Substances}/definition`)).status() !== 404) return;
  await ok(await api.post('/api/v1/registries', { data: { code: Substances, nameL10n: { en: 'Air substances' }, isTemporal: false } }), 'створення довідника речовин');
  await ok(
    await api.put(`/api/v1/registries/${Substances}/definition`, {
      headers: { 'If-Match': '1' },
      data: {
        fields: [{ id: null, code: 'SUBSTANCE_CODE', nameL10n: name('Code'), dataType: 'String', ordinal: 1, isRequired: true, isKey: true, lookupRegistryDefId: null, unitId: null }],
        rules: [], reason: 'e2e fixture: substances',
      },
    }),
    'опис довідника речовин',
  );
  const items = ['0301', '0337', '0330'].map((code) => ({
    clientRowId: code, op: 'upsert', id: null, code: `E${String(Number(code))}`, baseVersion: null, values: { SUBSTANCE_CODE: String(Number(code)) },
  }));
  const batch = await ok<{ applied: boolean }>(await api.post(`/api/v1/registries/${Substances}/entries/batch?dryRun=false`, { data: { items } }), 'речовини');
  expect(batch.applied, 'речовини не застосовані').toBe(true);
}

/** Registry `code` with one optional text field, if it is not there yet; returns its definition id. */
async function ensureRegistry(api: APIRequestContext, code: string, name: string): Promise<number> {
  if ((await api.get(`/api/v1/registries/${code}/definition`)).status() === 404) {
    await ok(await api.post('/api/v1/registries', { data: { code, nameL10n: { en: name }, isTemporal: false } }), `registry ${code}`);
    await ok(
      await api.put(`/api/v1/registries/${code}/definition`, {
        headers: { 'If-Match': '1' },
        data: {
          fields: [{ id: null, code: 'NOTE', nameL10n: { values: { en: 'Note' } }, dataType: 'String', ordinal: 1, isRequired: false, isKey: false, lookupRegistryDefId: null, unitId: null }],
          rules: [],
          reason: 'e2e fixture: air import scope',
        },
      }),
      `definition ${code}`,
    );
  }

  return (await ok<{ id: number }>(await api.get(`/api/v1/registries/${code}/definition`), code)).id;
}

/** Adds an entry named `name` (English display) to `registry`; returns its id. */
export async function addScopeEntry(api: APIRequestContext, registry: string, code: string, name: string): Promise<number> {
  const defId = await ensureRegistry(api, registry, registry === RegionRegistry ? 'Regions' : 'Areas');
  const created = await ok<{ id: number }>(
    await api.post(`/api/v1/registries/${registry}/entries`, {
      data: { registryDefId: defId, code, display: { values: { en: name } }, parentEntryId: null, levelCode: null, values: {} },
    }),
    `entry ${registry}/${code}`,
  );

  return created.id;
}

export async function scopeFields(api: APIRequestContext): Promise<ScopeFields> {
  return ok<ScopeFields>(await api.get('/api/v1/air-limits/scope-fields'), 'scope-fields');
}

/**
 * The Area entry that maps to NAE area `naeArea` (OFFSHORE): an existing one (provisioning may have created `AIR_AREA`), else a new
 * one named like the area («Offshore» normalises to OFFSHORE). Returns its id and whether the spec created it.
 */
export async function ensureAreaEntry(api: APIRequestContext, naeArea: string, stamp: string): Promise<{ id: number; created: boolean; name: string }> {
  const existing = (await scopeFields(api)).area.options.find((option) => option.naeArea === naeArea && option.entryId !== null);
  if (existing !== undefined && existing.entryId !== null) return { id: existing.entryId, created: false, name: existing.name };
  const name = naeArea.charAt(0) + naeArea.slice(1).toLowerCase();

  return { id: await addScopeEntry(api, AreaRegistry, `E2EA_${stamp}`, name), created: true, name };
}

/** Removes the entries the spec created (problems are returned, not thrown: cleanup continues). */
export async function removeScopeEntries(api: APIRequestContext, entries: readonly { registry: string; id: number }[]): Promise<string[]> {
  const problems: string[] = [];
  for (const entry of entries) {
    const response = await api.delete(`/api/v1/registries/${entry.registry}/entries/${String(entry.id)}`);
    if (response.status() >= 300 && response.status() !== 404) {
      problems.push(`${entry.registry}/${String(entry.id)}: ${String(response.status())} ${(await response.text()).slice(0, 300)}`);
    }
  }

  return problems;
}
