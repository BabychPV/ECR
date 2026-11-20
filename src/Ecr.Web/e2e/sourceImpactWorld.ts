import { execFileSync } from 'node:child_process';
import path from 'node:path';
import { expect, type APIResponse, type Page } from '@playwright/test';

/**
 * Самодостатні дані для `sourceImpact.spec.ts` (B80 N5, D-554): документ-джерело на шаблоні із зв'язкою
 * `cfg.DocumentSourceBinding` у стані `ApprovedOnly` і документи-споживачі (затверджений та лише поданий місяць).
 *
 * Усе, що дає API, заводиться API: довідник, шаблон-джерело (таблиця `FG_Internal` — потік складу HSE400, за яким
 * зв'язка впізнає свої таблиці), шаблон-споживач (формула читає довідник — так граф залежностей знаходить споживача),
 * проєкти, документи, подання й затвердження. API НЕ дає лише самої зв'язки й політики (`Warn / ConfirmWithReason /
 * Block`) — їх вставляє `tools/e2e/source-impact-binding.sql` через `sqlcmd` у тимчасову базу стенда (змінні
 * `ECR_E2E_SQL_*` виставляє `tools/e2e-stand.ps1`). Тестових ендпоінтів у продукті немає й не додається.
 *
 * ⚠ Кожен «світ» має власні шаблони, довідник і зв'язку (унікальний `stamp`), тож сценарії не заважають один одному й
 * сусіднім специфікаціям: завантажувач бере лише документ САМЕ цього шаблону-джерела.
 */
export const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

export type Policy = 'None' | 'Warn' | 'ConfirmWithReason' | 'Block';

const PolicyCode: Record<Policy, number> = { None: 0, Warn: 1, ConfirmWithReason: 2, Block: 3 };

/** Код таблиці — один із потоків складу HSE400 (`Hse400CompositionMapping.Streams`). */
const StreamTableCode = 'FG_Internal';

export async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

function column(ordinal: number, header: string, dataType: string): Record<string, unknown> {
  return {
    headerL10n: { en: header },
    ordinal,
    dataType,
    isRequired: false,
    isReadOnly: false,
    isHidden: false,
    precision: 18,
    scale: 2,
    defaultValue: null,
    displayFormat: null,
    lookupFilter: null,
    lookupRegistryDefId: null,
    styleId: null,
    unitId: null,
  };
}

export interface SourceImpactConsumerDto {
  bindingCode: string;
  templateCode: string;
  documentId: number;
  projectCode: string;
  documentKey: string;
  periodKey: number;
  approved: boolean;
  willRecalculate: boolean;
  skipReason: string | null;
  sheets: { sheetDefId: number; sheetCode: string; approved: boolean; willRecalculate: boolean }[];
}

export interface SourceImpactDto {
  action: string;
  policy: Policy;
  required: boolean;
  blocked: boolean;
  canRecalculateApproved: boolean;
  reasonRequired: boolean;
  impactHash: string;
  sourceMonths: number[];
  dependentCount: number;
  approvedCount: number;
  consumers: SourceImpactConsumerDto[];
}

interface DocumentCard {
  businessKey: string;
  projectId: number;
}

export interface DocRef {
  readonly documentId: number;
  readonly projectCode: string;
  readonly documentKey: string;
  readonly sheetDefId: number;
  readonly sheetCode: string;
}

export interface ImpactWorld {
  readonly stamp: string;
  readonly periodKey: number;
  readonly source: DocRef;
  readonly sourceTemplateCode: string;
  readonly consumerTemplateCode: string;
  /** Споживач, чий місяць ЗАТВЕРДЖЕНО. */
  readonly approvedConsumer: DocRef;
  /** Споживач, чий місяць лише подано (перерахується сам). */
  readonly pendingConsumer: DocRef;
  readonly bindingCode: string;
}

/** Чи є в оточенні все, щоб вставити зв'язку (`tools/e2e-stand.ps1` виставляє `ECR_E2E_SQL_*`). */
export function bindingSqlAvailable(): boolean {
  return (process.env['ECR_E2E_SQL_SERVER'] ?? '') !== '' && (process.env['ECR_E2E_SQL_DATABASE'] ?? '') !== '';
}

/** Вставляє зв'язку HSE400-типу для шаблону-джерела й довідника: `ApprovedOnly`, читання й перерахунок увімкнено. */
function insertBinding(bindingCode: string, sourceTemplateCode: string, registryCode: string, policy: Policy): void {
  const server = process.env['ECR_E2E_SQL_SERVER'] ?? '';
  const database = process.env['ECR_E2E_SQL_DATABASE'] ?? '';
  const login = process.env['ECR_E2E_SQL_LOGIN'] ?? '';
  for (const [name, value] of [['bindingCode', bindingCode], ['sourceTemplateCode', sourceTemplateCode], ['registryCode', registryCode]] as const) {
    // Значення вставляються у sqlcmd -v: лише літери, цифри, підкреслення (власні коди світу).
    expect(value, `${name} для SQL`).toMatch(/^[A-Za-z0-9_]+$/);
  }

  const script = path.resolve(process.cwd(), '..', '..', 'tools', 'e2e', 'source-impact-binding.sql');
  const args = ['-S', server, ...(login === '' ? ['-E'] : ['-U', login]), '-C', '-b', '-d', database, '-i', script, '-v',
    `BindingCode=${bindingCode}`, `SourceTemplateCode=${sourceTemplateCode}`, `RegistryCode=${registryCode}`,
    `Policy=${String(PolicyCode[policy])}`];
  try {
    execFileSync('sqlcmd', args, { encoding: 'utf8', stdio: 'pipe' });
  } catch (error) {
    const failure = error as { stdout?: string; stderr?: string };
    throw new Error(`sqlcmd (зв'язка ${bindingCode}): ${failure.stdout ?? ''} ${failure.stderr ?? ''}`);
  }
}

async function createTemplate(
  page: Page,
  code: string,
  build: (base: string) => Promise<void>,
): Promise<{ templateId: number; versionId: number; base: string }> {
  const template = await ok<{ templateId: number }>(
    await page.request.post('/api/v1/templates', { data: { code, nameL10n: { en: code } } }),
    `шаблон ${code}`,
  );
  const version = await ok<{ versionId: number }>(
    await page.request.post(`/api/v1/templates/${String(template.templateId)}/versions`, {
      data: { versionNumber: '1.0.0.0', cloneFromVersionId: null },
    }),
    `версія ${code}`,
  );
  const base = `/api/v1/template-versions/${String(version.versionId)}`;
  await build(base);
  await ok(await page.request.post(`${base}/publish`, { data: { reason: 'e2e: B80 N5 вплив джерела' } }), `публікація ${code}`);

  return { templateId: template.templateId, versionId: version.versionId, base };
}

async function createProject(page: Page, code: string, versionId: number): Promise<number> {
  const policies = await ok<{ id: number; code: string }[]>(await page.request.get('/api/v1/projects/period-policies'), 'політики періодів');
  const policy = policies.find((item) => item.code === 'ECR-Standard') ?? policies[0];
  const project = await ok<{ projectId: number }>(
    await page.request.post('/api/v1/projects', {
      data: {
        code,
        nameL10n: { en: code },
        periodKind: 'Monthly',
        timeZoneId: 'UTC',
        templateVersionId: versionId,
        periodPolicyId: policy?.id,
        year: new Date().getUTCFullYear(),
      },
    }),
    `проєкт ${code}`,
  );
  await ok(await page.request.post(`/api/v1/projects/${String(project.projectId)}/activate`), `активація ${code}`);

  return project.projectId;
}

/** Документ у проєкті; `projectCode` — код, з яким проєкт щойно створено (сервер віддає його в оцінці впливу). */
async function createDocument(
  page: Page,
  project: { readonly id: number; readonly code: string },
  versionId: number,
  base: string,
): Promise<DocRef> {
  const projectId = project.id;
  const structure = await ok<{ sheets: { id: number; code: string }[] }>(await page.request.get(`${base}/structure`), 'структура');
  const sheet = structure.sheets[0];
  expect(sheet, 'у шаблоні немає аркуша').toBeDefined();
  const created = await ok<{ documentId: number }>(
    await page.request.post('/api/v1/documents', {
      data: { projectId, sheetDefIds: [sheet?.id], templateVersionId: versionId },
    }),
    'документ',
  );

  const card = await ok<DocumentCard>(
    await page.request.get(`/api/v1/documents/${String(created.documentId)}`),
    'картка документа',
  );
  expect(card.projectId, 'документ не в тому проєкті').toBe(projectId);

  // ⛔ Не шукати код у `GET /projects`: він сторінковий (50 за замовчуванням), і на стенді з сотнями проєктів
  // свіжого проєкту на першій сторінці немає — код ставав порожнім і тест падав не з вини продукту.
  return {
    documentId: created.documentId,
    projectCode: project.code,
    documentKey: card.businessKey,
    sheetDefId: sheet?.id ?? 0,
    sheetCode: sheet?.code ?? '',
  };
}

/** Подає (і за потреби затверджує) оригінал аркуша документа за період. */
export async function submitSheet(page: Page, doc: DocRef, periodKey: number, approve: boolean): Promise<void> {
  await ok(
    await page.request.post(`/api/v1/documents/${String(doc.documentId)}/submit`, {
      data: { sheetDefId: doc.sheetDefId, periodKey, acknowledgeWarnings: true },
    }),
    `submit ${doc.documentKey}`,
  );
  if (approve) {
    await ok(
      await page.request.post(`/api/v1/documents/${String(doc.documentId)}/approve`, {
        data: { sheetDefId: doc.sheetDefId, periodKey, approved: true, reason: null },
      }),
      `approve ${doc.documentKey}`,
    );
  }
}

export async function sheetStatus(page: Page, doc: DocRef, periodKey: number): Promise<string> {
  const card = await ok<{ sheets?: { code: string; status?: string; state?: string }[] | null }>(
    await page.request.get(`/api/v1/documents/${String(doc.documentId)}?periodKey=${String(periodKey)}`),
    'картка документа',
  );
  const sheet = card.sheets?.find((item) => item.code === doc.sheetCode);

  return sheet?.status ?? sheet?.state ?? 'unknown';
}

export async function fetchImpact(page: Page, world: ImpactWorld, action: 'approve' | 'reopen' | 'edit'): Promise<SourceImpactDto> {
  return ok<SourceImpactDto>(
    await page.request.get(
      `/api/v1/documents/${String(world.source.documentId)}/source-impact?sheetDefId=${String(world.source.sheetDefId)}&periodKey=${String(world.periodKey)}&action=${action}`,
    ),
    `source-impact ${action}`,
  );
}

/** Рядок зі значенням у колонці A першої таблиці документа-споживача за період (до подання). */
async function fillConsumer(page: Page, doc: DocRef, periodKey: number): Promise<void> {
  const tables = await ok<{ tableInstanceId: number }[]>(
    await page.request.get(`/api/v1/documents/${String(doc.documentId)}/tables?periodKey=${String(periodKey)}`),
    `таблиці ${doc.documentKey}`,
  );
  const tableInstanceId = tables[0]?.tableInstanceId ?? 0;
  expect(tableInstanceId, `у ${doc.documentKey} немає екземпляра таблиці`).toBeGreaterThan(0);
  await ok(
    await page.request.post(`/api/v1/documents/${String(doc.documentId)}/rows`, { data: { tableInstanceId, rowKey: null } }),
    `рядок ${doc.documentKey}`,
  );
  const slice = await ok<{ rows: { rowKey: string; rowVersion: string }[] }>(
    await page.request.get(`/api/v1/documents/${String(doc.documentId)}/tables/${String(tableInstanceId)}`),
    `зріз ${doc.documentKey}`,
  );
  await ok(
    await page.request.patch(`/api/v1/documents/${String(doc.documentId)}/cells`, {
      data: {
        tableInstanceId,
        periodKey,
        origin: 'UserEdit',
        rows: slice.rows.map((row) => ({
          rowKey: row.rowKey,
          baseVersion: row.rowVersion,
          cells: [{ columnCode: 'A', value: '5', isEmpty: false }],
        })),
      },
    }),
    `значення ${doc.documentKey}`,
  );
}

/**
 * Будує «світ»: джерело (затверджене), споживач із затвердженим місяцем і споживач із поданим; потім вставляє
 * зв'язку з політикою й чекає, поки сервер побачить обох залежних (граф залежностей кешується з вікном ревізії).
 */
export async function createImpactWorld(page: Page, policy: Policy): Promise<ImpactWorld> {
  const stamp = `${String(Date.now())}${String(Math.floor(Math.random() * 1000))}`;
  const registryCode = `E2E_SI_REG_${stamp}`;
  const sourceCode = `E2ESISRC${stamp}`;
  const consumerCode = `E2ESICON${stamp}`;
  const bindingCode = `E2E_SI_${stamp}`;

  await ok(
    await page.request.post('/api/v1/registries', {
      data: { code: registryCode, nameL10n: { en: `E2E impact registry ${stamp}` }, isTemporal: false },
    }),
    'довідник',
  );

  // Джерело: аркуш з таблицею-потоком складу.
  const source = await createTemplate(page, sourceCode, async (base) => {
    await ok(
      await page.request.put(`${base}/sheets/S1`, {
        data: { nameL10n: { en: 'Composition' }, ordinal: 1, isMandatory: true, isVisible: true, sheetGroup: null },
      }),
      'аркуш джерела',
    );
    const table = await ok<{ id: number }>(
      await page.request.put(`${base}/sheets/S1/tables/${StreamTableCode}`, {
        data: { nameL10n: { en: StreamTableCode }, ordinal: 0, layoutKind: 'Static', rowMode: 'Dynamic', maxDynamicRows: 50 },
      }),
      'таблиця потоку',
    );
    await ok(await page.request.put(`${base}/tables/${String(table.id)}/columns/A`, { data: column(0, 'A', 'Decimal') }), 'колонка джерела');
  });

  // Споживач: формула читає довідник — за цим граф залежностей знаходить шаблон-споживача.
  const consumer = await createTemplate(page, consumerCode, async (base) => {
    await ok(
      await page.request.put(`${base}/sheets/S1`, {
        data: { nameL10n: { en: 'Land' }, ordinal: 1, isMandatory: true, isVisible: true, sheetGroup: null },
      }),
      'аркуш споживача',
    );
    const table = await ok<{ id: number }>(
      await page.request.put(`${base}/sheets/S1/tables/T1`, {
        data: { nameL10n: { en: 'Land' }, ordinal: 0, layoutKind: 'Static', rowMode: 'Dynamic', maxDynamicRows: 50 },
      }),
      'таблиця споживача',
    );
    for (const [index, [code, dataType]] of [['A', 'Decimal'], ['F', 'Formula']].entries()) {
      await ok(
        await page.request.put(`${base}/tables/${String(table.id)}/columns/${code ?? ''}`, { data: column(index, code ?? '', dataType ?? 'Decimal') }),
        `колонка ${code ?? ''}`,
      );
    }

    const draft = await ok<{ sheets: { tables: { id: number; columns: { id: number; code: string }[] }[] }[] }>(
      await page.request.get(`${base}/structure`),
      'структура споживача',
    );
    const formulaColumn = draft.sheets[0]?.tables[0]?.columns.find((candidate) => candidate.code === 'F')?.id ?? 0;
    await ok(
      await page.request.put(`${base}/tables/${String(table.id)}/formulas/column/${String(formulaColumn)}`, {
        data: { dialect: 'Template', expression: `REGCOUNT('${registryCode}', ROW.VALUE > 0)` },
      }),
      'формула споживача',
    );
  });

  const sourceCode = `E2ESISP${stamp}`;
  const consumerCode = `E2ESICP${stamp}`;
  const sourceProject = { id: await createProject(page, sourceCode, source.versionId), code: sourceCode };
  const consumerProject = { id: await createProject(page, consumerCode, consumer.versionId), code: consumerCode };
  const sourceDoc = await createDocument(page, sourceProject, source.versionId, source.base);
  const approvedConsumer = await createDocument(page, consumerProject, consumer.versionId, consumer.base);
  const pendingConsumer = await createDocument(page, consumerProject, consumer.versionId, consumer.base);

  const calendar = await ok<{ periods: { periodKey: number; state: string }[] }>(
    await page.request.get(`/api/v1/projects/${String(sourceProject.id)}/periods`),
    'календар джерела',
  );
  const periodKey = (calendar.periods.find((item) => item.state === 'Open') ?? calendar.periods[0])?.periodKey ?? 0;
  expect(periodKey, 'у проєкту джерела немає періоду').toBeGreaterThan(0);

  // Екземпляр таблиці-потоку за період (його читає вибір документа-джерела): рядок створює таблицю, якщо її ще немає.
  const tables = await ok<{ tableInstanceId: number | null; sheetDefId: number; tableCode?: string }[]>(
    await page.request.get(`/api/v1/documents/${String(sourceDoc.documentId)}/tables?periodKey=${String(periodKey)}`),
    'таблиці джерела',
  );
  expect(tables.length, 'у джерела немає таблиць').toBeGreaterThan(0);

  // Споживачі мають дані (рядок зі значенням у колонці A): без них перерахунок не має що перераховувати.
  for (const consumerDoc of [approvedConsumer, pendingConsumer]) {
    await fillConsumer(page, consumerDoc, periodKey);
  }

  // Стани аркушів: джерело й один споживач затверджено, другий споживач подано.
  await submitSheet(page, sourceDoc, periodKey, true);
  await submitSheet(page, approvedConsumer, periodKey, true);
  await submitSheet(page, pendingConsumer, periodKey, false);

  insertBinding(bindingCode, sourceCode, registryCode, policy);

  const world: ImpactWorld = {
    stamp,
    periodKey,
    source: sourceDoc,
    sourceTemplateCode: sourceCode,
    consumerTemplateCode: consumerCode,
    approvedConsumer,
    pendingConsumer,
    bindingCode,
  };
  await expect
    .poll(async () => (await fetchImpact(page, world, 'reopen')).dependentCount, {
      message: 'сервер не побачив залежних: зв’язка/граф не підхопилися',
      timeout: 90_000,
      intervals: [1_000, 2_000, 5_000],
    })
    .toBe(2);

  return world;
}
