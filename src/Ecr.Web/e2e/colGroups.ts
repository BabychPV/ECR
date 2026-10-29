import { expect, type APIResponse, type Page } from '@playwright/test';

/**
 * Фікстура e2e «групові заголовки колонок» (ui-colgroups): шаблон з однією таблицею з 12 колонками й
 * деревом груп у 3 рівні, публікація, проєкт і документ — усе через API, як `multiSheet.ts`.
 *
 *   C1                          — без групи
 *   Emissions
 *     Q1
 *       Jan: C2 C3   Feb: C4 C5
 *     Q2: C6 C7
 *     (напряму в Emissions): C8
 *   Fuel: C9 C10 C11
 *   C12                         — без групи
 */
export const GroupedColumns = 12;

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

function column(ordinal: number, header: string): Record<string, unknown> {
  return {
    headerL10n: { en: header },
    ordinal,
    dataType: 'Decimal',
    isRequired: false,
    isReadOnly: false,
    isHidden: false,
    precision: 18,
    scale: 4,
    defaultValue: null,
    displayFormat: null,
    lookupFilter: null,
    lookupRegistryDefId: null,
    styleId: null,
    unitId: null,
  };
}

export interface GroupedDocument {
  readonly documentId: number;
  readonly periodKey: number;
  readonly versionId: number;
  readonly tableId: number;
}

const GroupTree = [
  { code: 'E', parentCode: null, ordinal: 0, labelL10n: { en: 'Emissions' } },
  { code: 'Q1', parentCode: 'E', ordinal: 0, labelL10n: { en: 'Q1' } },
  { code: 'JAN', parentCode: 'Q1', ordinal: 0, labelL10n: { en: 'Jan' } },
  { code: 'FEB', parentCode: 'Q1', ordinal: 1, labelL10n: { en: 'Feb' } },
  { code: 'Q2', parentCode: 'E', ordinal: 1, labelL10n: { en: 'Q2' } },
  { code: 'F', parentCode: null, ordinal: 1, labelL10n: { en: 'Fuel' } },
];

const GroupOfColumn: Record<string, string> = {
  C2: 'JAN',
  C3: 'JAN',
  C4: 'FEB',
  C5: 'FEB',
  C6: 'Q2',
  C7: 'Q2',
  C8: 'E',
  C9: 'F',
  C10: 'F',
  C11: 'F',
};

export async function createGroupedDocument(page: Page): Promise<GroupedDocument> {
  const stamp = String(Date.now());
  const template = await ok<{ templateId: number }>(
    await page.request.post('/api/v1/templates', { data: { code: `E2EGroups${stamp}`, nameL10n: { en: `E2E groups ${stamp}` } } }),
    'шаблон',
  );
  const version = await ok<{ versionId: number }>(
    await page.request.post(`/api/v1/templates/${String(template.templateId)}/versions`, {
      data: { versionNumber: '1.0.0.0', cloneFromVersionId: null },
    }),
    'версія шаблону',
  );
  const base = `/api/v1/template-versions/${String(version.versionId)}`;

  await ok(
    await page.request.put(`${base}/sheets/S1`, {
      data: { nameL10n: { en: 'Sheet 1' }, ordinal: 1, isMandatory: true, isVisible: true, sheetGroup: null },
    }),
    'аркуш',
  );
  const table = await ok<{ id: number }>(
    await page.request.put(`${base}/sheets/S1/tables/T1`, {
      data: { nameL10n: { en: 'Table 1' }, ordinal: 0, layoutKind: 'Static', rowMode: 'Dynamic', maxDynamicRows: 50 },
    }),
    'таблиця',
  );

  for (let c = 1; c <= GroupedColumns; c += 1) {
    await ok(
      await page.request.put(`${base}/tables/${String(table.id)}/columns/C${String(c)}`, { data: column(c - 1, `C${String(c)}`) }),
      `колонка C${String(c)}`,
    );
  }

  await ok(await page.request.post(`${base}/publish`, { data: { reason: 'e2e: групові заголовки' } }), 'публікація версії');

  // Групи — презентаційна операція, тож працює й після публікації.
  const replaced = await ok<{ groups: { id: number; code: string }[] }>(
    await page.request.put(`${base}/tables/${String(table.id)}/header-groups`, { data: { groups: GroupTree } }),
    'дерево груп',
  );
  const groupId = new Map(replaced.groups.map((group) => [group.code, group.id] as const));
  const structure = await ok<{ sheets: { id: number; tables: { id: number; columns: { id: number; code: string }[] }[] }[] }>(
    await page.request.get(`${base}/structure`),
    'структура версії',
  );
  const columns = structure.sheets[0]?.tables.find((item) => item.id === table.id)?.columns ?? [];
  const changes = columns.flatMap((item) => {
    const group = GroupOfColumn[item.code];

    return group === undefined
      ? []
      : [{ entityType: 'ColumnDef', entityId: item.id, field: 'HeaderGroupId', value: String(groupId.get(group)) }];
  });
  expect(changes, 'колонки, яким призначено групи').toHaveLength(Object.keys(GroupOfColumn).length);
  await ok(await page.request.patch(`${base}/presentation`, { data: changes }), 'призначення груп колонкам');

  const policies = await ok<{ id: number; code: string }[]>(await page.request.get('/api/v1/projects/period-policies'), 'політики періодів');
  const policy = policies.find((item) => item.code === 'ECR-Standard') ?? policies[0];
  const project = await ok<{ projectId: number }>(
    await page.request.post('/api/v1/projects', {
      data: {
        code: `E2EGroupsPrj${stamp}`,
        nameL10n: { en: `E2E groups project ${stamp}` },
        periodKind: 'Monthly',
        timeZoneId: 'UTC',
        templateVersionId: version.versionId,
        periodPolicyId: policy?.id,
        year: new Date().getUTCFullYear(),
      },
    }),
    'проєкт',
  );
  await ok(await page.request.post(`/api/v1/projects/${String(project.projectId)}/activate`), 'активація проєкту');
  const sheets = await ok<{ sheets: { id: number }[] }>(await page.request.get(`${base}/structure`), 'структура для документа');
  const document = await ok<{ documentId: number }>(
    await page.request.post('/api/v1/documents', {
      data: { projectId: project.projectId, sheetDefIds: sheets.sheets.map((sheet) => sheet.id), templateVersionId: version.versionId },
    }),
    'документ',
  );
  const calendar = await ok<{ periods: { periodKey: number; state: string }[] }>(
    await page.request.get(`/api/v1/projects/${String(project.projectId)}/periods`),
    'календар проєкту',
  );
  const period = calendar.periods.find((item) => item.state === 'Open') ?? calendar.periods[0];
  expect(period, 'у проєкті немає періодів').toBeDefined();

  return { documentId: document.documentId, periodKey: period?.periodKey ?? 0, versionId: version.versionId, tableId: table.id };
}
