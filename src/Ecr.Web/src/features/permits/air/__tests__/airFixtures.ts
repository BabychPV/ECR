import type {
  AirAnalysis,
  AirImportResult,
  AirLimitsPermit,
  AirPreview,
  AirScopeCodes,
  AirSource,
  ImportProfile,
  ImportProfileSummary,
  NaeTree,
} from '../airTypes';

export function substance(id: number, code: string, name: string, ty: string | null = '1.5'): AirLimitsPermit['sources'][number]['substances'][number] {
  return {
    id,
    substanceEntryId: 900 + id,
    substanceCode: code,
    substanceName: name,
    gramsPerSecond: '0.25',
    mgPerNm3: '12.5',
    tonsPerYear: ty,
  };
}

export function source(id: number, nae: string, patch: Partial<AirSource> = {}): AirSource {
  return {
    id,
    naeEntryId: 500 + id,
    naeCode: nae,
    region: 'ATYRAU',
    area: 'OFFSHORE',
    productionCode: null,
    siteLabel: 'Barges',
    sourceName: `Source ${nae}`,
    emissionPointName: 'Exhaust pipe',
    sourceType: 'T',
    quantity: '4',
    hoursPerYear: '2400',
    height: '6.5',
    diameter: '0.08',
    velocity: null,
    volume: null,
    temperature: '35',
    x1: null,
    y1: null,
    x2: null,
    y2: null,
    substances: [substance(id * 10 + 1, '0301', 'Nitrogen dioxide'), substance(id * 10 + 2, '0337', 'Carbon oxide', null)],
    ...patch,
  };
}

export function card(patch: Partial<AirLimitsPermit> = {}): AirLimitsPermit {
  return {
    permitId: 5,
    permitNumber: 'KZ87VCZ14622157',
    typeCode: 'AIR_LIMITS',
    validFrom: '2024-01-01',
    validTo: '2027-01-01',
    sources: [source(1, '1085'), source(2, '6001', { sourceType: 'P1', sourceName: 'Wellheads', substances: [substance(21, '0333', 'Hydrogen sulphide')] })],
    substanceRows: 3,
    totals: [
      { substanceCode: '0301', substanceEntryId: 1, substanceName: 'Nitrogen dioxide', sources: 1, gramsPerSecond: '0.25', tonsPerYear: '1.5' },
      { substanceCode: '0333', substanceEntryId: 2, substanceName: 'Hydrogen sulphide', sources: 1, gramsPerSecond: '0.25', tonsPerYear: '1.5' },
    ] as AirLimitsPermit['totals'],
    ...patch,
  };
}

export const profiles: ImportProfileSummary[] = [
  { code: 'ERA_TABLE_33', builtIn: true, nameL10n: { en: 'ERA table 3.3' }, updatedAt: '2026-10-01T00:00:00Z' },
];

export function profile(): ImportProfile {
  return {
    code: 'ERA_TABLE_33',
    builtIn: true,
    nameL10n: { en: 'ERA table 3.3' },
    rowVersion: 'rv1',
    updatedAt: '2026-10-01T00:00:00Z',
    spec: {
      fields: [
        { name: 'NaeCode', patterns: ['Source number', 'Номер источника'], level: 'Parent', parser: 'Code', required: true },
        { name: 'TonsPerYear', patterns: ['t/year'], level: 'Child', parser: 'Decimal', required: true },
      ],
    },
  };
}

export function analysis(patch: Partial<AirAnalysis['analysis']> = {}): AirAnalysis {
  return {
    profileCode: 'ERA_TABLE_33',
    analysis: {
      canContinue: true,
      droppedIssues: 0,
      fileSha256: 'abc',
      groups: [],
      header: {
        headerFromRow: 4,
        headerToRow: 6,
        dataFromRow: 9,
        mapped: [
          { column: 7, columnLetter: 'G', field: 'NaeCode', headerText: 'Source number', level: 'Parent' },
          { column: 27, columnLetter: 'AA', field: 'TonsPerYear', headerText: 't/year', level: 'Child' },
        ],
        unmapped: [{ column: 2, columnLetter: 'B', headerText: 'Shop', hasData: false }],
      },
      issues: [],
      sheetName: 'Sheet1',
      sheetNames: ['Sheet1'],
      stats: { parents: 373, children: 2624, distinctChildKeys: 55, groupRows: 27, skippedRows: 5 },
      ...patch,
    },
  };
}

export function preview(patch: Partial<AirPreview['preview']> = {}): AirPreview {
  return {
    permitId: 5,
    permitNumber: 'KZ87VCZ14622157',
    region: 'ATYRAU',
    area: 'OFFSHORE',
    profileCode: 'ERA_TABLE_33',
    preview: {
      canApply: true,
      droppedIssues: 0,
      fingerprint: 'fp-1',
      groups: [],
      header: null,
      issues: [
        {
          severity: 'Warning',
          messageKey: 'err.ECR-IMP-0422.regValueEmpty',
          row: 31,
          column: 'AA',
          field: 'TonsPerYear',
          args: { row: '31' },
        },
      ],
      missing: [
        { kind: 'nae', code: '3544', groupLabel: '211 Island DC05', resolved: null, row: 40 },
        { kind: 'nae', code: '3545', groupLabel: '211 Island DC05', resolved: null, row: 41 },
      ],
      replace: {
        confirmationRequired: true,
        thisOwner: { ownerKey: '5', label: 'KZ87VCZ14622157', parents: 371, children: 2610 },
        otherOwners: [
          { ownerKey: '7', label: 'KZ46VCZ14622410', parents: 12, children: 96 },
          { ownerKey: '9', label: 'KZ79UKR00003155', parents: 3, children: 24 },
        ],
      },
      diff: { newParents: 2, changedParents: 10, unchangedParents: 361, removedParents: 0, skippedParents: 0, children: 2624 },
      stats: { parents: 373, children: 2624, distinctChildKeys: 55, groupRows: 27, skippedRows: 5 },
      ...patch,
    },
  };
}

export function result(): AirImportResult {
  return {
    permitId: 5,
    permitNumber: 'KZ87VCZ14622157',
    region: 'ATYRAU',
    area: 'OFFSHORE',
    sources: 373,
    substances: 2624,
    createdNae: 2,
    replacedOwn: 371,
    replacedOthers: [{ ownerKey: '7', label: 'KZ46VCZ14622410', parents: 12, children: 96 }],
    diff: { newParents: 2, changedParents: 10, unchangedParents: 361, removedParents: 0, skippedParents: 0, children: 2624 },
    fingerprint: 'fp-1-abcdefghijkl',
    fileSha256: 'abc',
  };
}

export function tree(): NaeTree {
  const node = (id: number, parent: number | null, display: string, levelCode: string) => ({
    id,
    parentEntryId: parent,
    code: `N${String(id)}`,
    display,
    levelCode,
    childCount: 0,
    isActive: true,
    isMatch: true,
    ordinal: id,
    validFrom: null,
    validTo: null,
  });

  return {
    dataRevision: 1,
    isHierarchical: true,
    levels: [],
    registryCode: 'NAE_TREE',
    nodes: [node(1, null, 'Atyrau', 'REGION'), node(2, 1, 'Offshore', 'AREA'), node(3, 2, 'DC05_Island', 'SITE'), node(4, 3, '1085', 'SRC')],
  };
}

export function xlsx(name = 'limits.xlsx'): File {
  return new File([new Uint8Array([80, 75, 3, 4])], name, {
    type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
  });
}

/** Scope codes of the NAE tree (GET /air-limits/scope-codes): two regions, one with a no-area code. */
export function scopeCodes(): AirScopeCodes {
  return {
    regions: [
      { code: 'ATYRAU', name: 'Atyrau', areas: [{ code: 'OFFSHORE', name: 'Offshore' }, { code: 'ONSHORE', name: null }] },
      { code: 'MANGISTAU', name: null, areas: [{ code: '-', name: null }] },
    ],
  };
}
