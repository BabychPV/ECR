import { vi } from 'vitest';
import type { RegistryDefDto, RegistryDefinitionDto } from '@/api/types';
import type { RegistryBatchRequest, RegistryRow } from '../../rows/api';

/**
 * Сервер редактора master-detail для тестів (`ФВ-8.16`): кейси потоку (`STREAM_CASE`) зі складом
 * газу (`GAS_COMPOSITION`, частина кейсу) і компонентами (`COMPONENT`, просте посилання складу).
 */

type Field = RegistryDefinitionDto['fields'][number];

function field(id: number, code: string, dataType: string, lookupRegistryDefId: number | null = null): Field {
  return {
    id,
    code,
    nameL10n: { values: { en: code } },
    dataType,
    isRequired: false,
    isScopeField: false,
    lookupRegistryDefId,
    unitId: null,
  };
}

const Cases: RegistryDefinitionDto = {
  id: 2,
  code: 'STREAM_CASE',
  nameL10n: { values: { en: 'Stream cases' } },
  isTemporal: false,
  sourceKind: 'Local',
  definitionVersion: 1,
  dataRevision: 1,
  codeMode: 'Auto',
  fields: [field(21, 'CASE_NAME', 'String')],
  relations: [],
  rules: [
    {
      id: 1,
      code: 'SUM_100',
      expression: '…',
      isActive: true,
      messageL10n: { values: {} },
      parametersJson: '{"template":"childSum","child":"GAS_COMPOSITION","field":"MOL_PCT","target":100,"tolerance":0.5}',
      ruleKind: 'Expression',
      severity: 'Warning',
    },
  ],
  mappings: [],
};

const Composition: RegistryDefinitionDto = {
  ...Cases,
  id: 3,
  code: 'GAS_COMPOSITION',
  nameL10n: { values: { en: 'Gas composition' } },
  fields: [field(31, 'CASE', 'Lookup', 2), field(32, 'COMPONENT', 'Lookup', 4), field(33, 'MOL_PCT', 'Decimal')],
  relations: [
    { kind: 'Composition', fieldCode: 'CASE', targetRegistryDefId: 2, targetRegistryCode: 'STREAM_CASE', linkKind: null, linkCount: null, onParentDelete: 'Cascade' },
    { kind: 'Cascade', fieldCode: 'COMPONENT', targetRegistryDefId: 4, targetRegistryCode: 'COMPONENT', linkKind: null, linkCount: null },
  ],
  rules: [],
};

const Component: RegistryDefinitionDto = { ...Cases, id: 4, code: 'COMPONENT', fields: [], rules: [] };

function listed(definition: RegistryDefinitionDto): RegistryDefDto {
  return {
    id: definition.id,
    code: definition.code,
    nameL10n: definition.nameL10n,
    fields: definition.fields,
    isHierarchical: false,
    isTemporal: false,
    sourceKind: 'Local',
  };
}

function row(id: number, code: string, values: Record<string, string>, display = code): RegistryRow {
  return {
    id,
    code,
    display,
    parentEntryId: null,
    validFrom: null,
    validTo: null,
    version: `v${id}`,
    values: Object.fromEntries(Object.entries(values).map(([key, value]) => [key, { value, display: null, unit: null }])),
  };
}

const CaseRows = [row(77, 'E77', { CASE_NAME: '370 Summer' }, '370 Summer'), row(78, 'E78', { CASE_NAME: '370 Winter' }, '370 Winter')];
const PartRows = [row(501, 'E501', { CASE: '77', COMPONENT: '3', MOL_PCT: '60' }), row(502, 'E502', { CASE: '77', COMPONENT: '5', MOL_PCT: '39.8' })];
const ComponentRows = [row(3, 'N2', {}, 'Nitrogen'), row(5, 'CH4', {}, 'Methane'), row(6, 'C2H6', {}, 'Ethane')];

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

function page(items: RegistryRow[]): unknown {
  return { items, nextCursor: null, totalCount: items.length };
}

export interface Server {
  readonly batches: { code: string; dryRun: string | null; body: RegistryBatchRequest }[];
  readonly rowQueries: string[];
}

export function mockServer(permissions: string[]): Server {
  const server: Server = { batches: [], rowQueries: [] };
  const definitions: Record<string, RegistryDefinitionDto> = { STREAM_CASE: Cases, GAS_COMPOSITION: Composition, COMPONENT: Component };

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), 'http://localhost');
      const path = url.pathname;

      if (path.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: {} });

      if (path.endsWith('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions,
          simulatedForUserId: null,
          userId: 9,
          userName: 'tester',
        });
      }

      if (path.endsWith('/api/v1/registries')) return json([Cases, Composition, Component].map(listed));

      const match = /\/api\/v1\/registries\/([^/]+)\/(definition|rows|entries\/batch)$/.exec(path);
      const code = decodeURIComponent(match?.[1] ?? '');

      if (match?.[2] === 'definition') return json(definitions[code]);

      if (match?.[2] === 'rows') {
        server.rowQueries.push(`${code}?${url.searchParams.toString()}`);
        if (code === 'STREAM_CASE') return json(page(CaseRows));
        if (code === 'COMPONENT') return json(page(ComponentRows));
        const parent = url.searchParams.get('parentEntryId');
        return json(page(PartRows.filter((part) => part.values['CASE']?.value === parent)));
      }

      if (match?.[2] === 'entries/batch') {
        const body = JSON.parse(String(init?.body)) as RegistryBatchRequest;
        server.batches.push({ code, dryRun: url.searchParams.get('dryRun'), body });
        return json({
          added: 1,
          applied: url.searchParams.get('dryRun') === 'false',
          deleted: 0,
          dryRun: url.searchParams.get('dryRun') === 'true',
          unchanged: 0,
          updated: 1,
          rows: body.items.map((item) => ({ clientRowId: item.clientRowId, entryId: item.id, status: 'updated', version: null, errors: [] })),
          rules: [],
        });
      }

      return json(null);
    }),
  );

  return server;
}

