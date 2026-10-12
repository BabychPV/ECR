import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  applyEditorSet,
  copyEditAssignment,
  createEditorSet,
  deleteEditAssignment,
  deleteEditorSet,
  fillEditAssignment,
  getCandidates,
  getEditAssignments,
  getEditorSets,
  getMyEditAssignments,
  returnEditAssignment,
  updateEditorSet,
  upsertEditAssignment,
  validateEditAssignment,
} from '../api';
import { makeSlice } from '../model';

/**
 * Відповідність `api.ts` серверному контракту (`contracts/openapi.snapshot.json`): кожен виклик - існуюча пара
 * «метод + шлях», тіло має всі обов'язкові поля схеми й жодного зайвого. Це те, що пропустили типи 5a-5c: вони
 * були узгоджені самі з собою (`PUT` замість `POST`, `owner` замість `ownerDisplayName`, `userIds` замість
 * `memberUserIds`) і дали б 405/400 на живому сервері.
 */

interface Schema {
  readonly $ref?: string;
  readonly type?: string;
  readonly required?: readonly string[];
  readonly properties?: Record<string, Schema>;
  readonly nullable?: boolean;
}

interface Operation {
  readonly requestBody?: { readonly content: Record<string, { readonly schema: Schema }> };
}

const spec = JSON.parse(readFileSync(resolve(process.cwd(), '../../contracts/openapi.snapshot.json'), 'utf8')) as {
  paths: Record<string, Record<string, Operation>>;
  components: { schemas: Record<string, Schema> };
};

const deref = (schema: Schema): Schema =>
  schema.$ref === undefined ? schema : (spec.components.schemas[schema.$ref.split('/').pop() ?? ''] ?? {});

interface Call {
  readonly method: string;
  readonly path: string;
  readonly body: unknown;
}

const calls: Call[] = [];

function stubFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      calls.push({
        method: (init?.method ?? 'GET').toUpperCase(),
        path: String(input).split('?')[0] ?? '',
        body: init?.body === undefined ? undefined : JSON.parse(String(init.body)),
      });

      return Promise.resolve(new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } }));
    }),
  );
}

/** Знаходить шаблон шляху зі схеми, якому відповідає фактична адреса. */
function templateOf(path: string): string | undefined {
  return Object.keys(spec.paths).find((template) =>
    new RegExp(`^${template.replace(/\{[^}]+\}/g, '[^/]+')}$`).test(path),
  );
}

/** Рекурсивно: обов'язкові поля є, зайвих немає (для вкладених об'єктів схеми теж). */
function violations(value: unknown, schema: Schema, at: string): string[] {
  const resolved = deref(schema);
  if (value === null || typeof value !== 'object' || Array.isArray(value) || resolved.properties === undefined) return [];

  const record = value as Record<string, unknown>;
  const known = resolved.properties;
  const out: string[] = [];

  for (const key of resolved.required ?? []) if (!(key in record)) out.push(`${at}.${key}: обов'язкове поле не надіслано`);
  for (const key of Object.keys(record)) {
    const child = known[key];
    if (child === undefined) out.push(`${at}.${key}: сервер такого поля не має`);
    else out.push(...violations(record[key], child, `${at}.${key}`));
  }

  return out;
}

const slice = makeSlice({ monthNumber: 1 });

async function callEverything(): Promise<void> {
  await getEditAssignments(7);
  await upsertEditAssignment(7, { slice, recipientUserIds: [1], isExclusive: true, note: 'n', appliedSetId: 3, baseVersion: 'v' });
  await deleteEditAssignment(7, 2);
  await validateEditAssignment(7, 2, 'v');
  await returnEditAssignment(7, 2, { reason: 'r', baseVersion: 'v', userIds: null });
  await fillEditAssignment(7, 2);
  await copyEditAssignment(7, { fromSlice: slice, toSlices: [makeSlice({ monthNumber: 2 })] });
  await applyEditorSet(7, { setId: 3, slices: [slice], isExclusive: true, note: null });
  await getCandidates(7, 'ko');
  await getEditorSets();
  await createEditorSet({ name: 'n', memberUserIds: [1] });
  await updateEditorSet(3, { name: 'n', memberUserIds: [1], baseVersion: 'v' });
  await deleteEditorSet(3);
  await getMyEditAssignments();
}

afterEach(() => {
  vi.unstubAllGlobals();
  calls.length = 0;
});

describe('api.ts передач ↔ openapi.snapshot.json', () => {
  it('кожен виклик - існуюча пара «метод + шлях» контракту', async () => {
    stubFetch();
    await callEverything();

    expect(calls.length).toBe(14);
    for (const call of calls) {
      const template = templateOf(call.path);
      expect(template, `${call.method} ${call.path}: такого шляху в контракті немає`).toBeDefined();
      expect(
        Object.keys(spec.paths[template ?? ''] ?? {}),
        `${call.method} ${call.path}: метод не підтримано сервером`,
      ).toContain(call.method.toLowerCase());
    }
  });

  it('тіла запитів збігаються зі схемою: усі обов\'язкові поля є, зайвих немає', async () => {
    stubFetch();
    await callEverything();

    const problems: string[] = [];
    for (const call of calls) {
      const operation = spec.paths[templateOf(call.path) ?? '']?.[call.method.toLowerCase()];
      const schema = operation?.requestBody?.content['application/json']?.schema;

      if (schema === undefined) {
        if (call.body !== undefined) problems.push(`${call.method} ${call.path}: сервер тіла не чекає`);
        continue;
      }
      problems.push(...violations(call.body, schema, `${call.method} ${call.path}`));
    }

    expect(problems).toEqual([]);
  });

  it('усі дії контролерів передач мають виклик у клієнті', async () => {
    stubFetch();
    await callEverything();

    const covered = new Set(calls.map((c) => `${c.method.toLowerCase()} ${templateOf(c.path)}`));
    const server = Object.entries(spec.paths)
      .filter(([path]) => path.includes('/edit-assignments') || path.includes('/editor-sets'))
      .flatMap(([path, methods]) => Object.keys(methods).map((m) => `${m} ${path}`));

    expect(server.filter((action) => !covered.has(action))).toEqual([]);
  });
});
