import { describe, expect, it } from 'vitest';
import { QueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import { resolveCrumbValue } from '@/app/breadcrumbResolvers';

/**
 * Резолвер динамічних крихт breadcrumbs (`PR nav-arch #3`).
 *
 * ⛔ Головна дисципліна цього файла — той самий клас перевірки, що й
 * `queryKeys.test.ts` (`PR #1`): не «функція щось повертає», а інваріант,
 * який справді захищає від регресу. Тут інваріант — резолвер читає РІВНО
 * той ключ кешу, який справді несе людиночитну назву (`templates.versionsOf`
 * для номера версії, НЕ `templates.version`, що кешує лише структуру
 * презентації без жодного людського підпису), і РІЗНИТЬ «запит іде» від
 * «запиту не буде ніколи» без жодного власного запиту.
 *
 * Мутаційна перевірка (RED → GREEN, вручну, проведена перед комітом):
 * `templateVersionLabelLookup` тимчасово зведено на `queryKeys.templates.version(versionId)`
 * (наївна форма «версія — це queryKeys.templates.version»). RED: тест
 * «читає версію САМЕ з versionsOf(templateId), не з version(versionId)»
 * нижче впав — `resolveCrumbValue` повернув `unavailable` замість `resolved`,
 * бо кеш під тим ключем ніс `TemplateStructureDto` без `.version`.
 * Відновлено — GREEN.
 */
function seedTemplateList(client: QueryClient): void {
  client.setQueryData(queryKeys.templates.list(), {
    items: [
      { id: 1, code: 'TPL1', versionCount: 2 },
      { id: 2, code: 'TPL2', versionCount: 1 },
    ],
    nextCursor: null,
    totalCount: 2,
  });
}

function seedVersionsOf(client: QueryClient, templateId: number): void {
  client.setQueryData(queryKeys.templates.versionsOf(templateId), {
    items: [
      {
        id: 7,
        version: '1.0',
        status: 'Draft',
        publishedAt: null,
        presentationRevision: 1,
        clonedFromVersionId: null,
      },
    ],
    nextCursor: null,
    totalCount: 1,
  });
}

function seedRegistryDefinition(client: QueryClient, code: string, nameEn: string): void {
  client.setQueryData(queryKeys.registries.definition(code), {
    code,
    id: 1,
    dataRevision: 1,
    definitionVersion: 1,
    fields: [],
    isTemporal: false,
    mappings: [],
    nameL10n: { values: { en: nameEn } },
    relations: [],
    rules: [],
    sourceKind: 'Manual',
  });
}

describe('resolveCrumbValue — templateName', () => {
  it('резолвить код шаблону з templates.list() за :id', () => {
    const client = new QueryClient();
    seedTemplateList(client);

    expect(resolveCrumbValue(client, 'templateName', { id: '1' })).toEqual({
      status: 'resolved',
      text: 'TPL1',
    });
    expect(resolveCrumbValue(client, 'templateName', { id: '2' })).toEqual({
      status: 'resolved',
      text: 'TPL2',
    });
  });

  it('холодний кеш без активного запиту — unavailable, не вічний скелет і не сирий :id', () => {
    const client = new QueryClient();

    expect(resolveCrumbValue(client, 'templateName', { id: '1' })).toEqual({
      status: 'unavailable',
    });
  });

  it('запит на templates.list() ще виконується — loading (Skeleton), а не unavailable', () => {
    const client = new QueryClient();
    // ⚠ Проміс навмисно ніколи не резолвиться в межах цього тесту — важливий
    // лише СИНХРОННИЙ стан `fetchStatus` одразу після виклику `fetchQuery`.
    void client.fetchQuery({
      queryKey: queryKeys.templates.list(),
      queryFn: () => new Promise(() => {}),
    });

    expect(resolveCrumbValue(client, 'templateName', { id: '1' })).toEqual({ status: 'loading' });
  });

  it('id, якого нема в переліку — unavailable, а не помилка', () => {
    const client = new QueryClient();
    seedTemplateList(client);

    expect(resolveCrumbValue(client, 'templateName', { id: '999' })).toEqual({
      status: 'unavailable',
    });
  });

  it('нечисловий :id — unavailable без звернення до кешу', () => {
    const client = new QueryClient();
    seedTemplateList(client);

    expect(resolveCrumbValue(client, 'templateName', { id: 'not-a-number' })).toEqual({
      status: 'unavailable',
    });
  });
});

describe('resolveCrumbValue — templateVersionLabel', () => {
  it('читає версію САМЕ з versionsOf(templateId), не з version(versionId)', () => {
    const client = new QueryClient();
    seedVersionsOf(client, 1);

    // ⛔ Навмисно засіваємо ІНШИЙ ключ (`templates.version`, структура без
    // людського підпису) сміттям без `.version` — якби резолвер читав звідси,
    // тест впав би на `undefined`/`unavailable` замість очікуваного `'1.0'`.
    client.setQueryData(queryKeys.templates.version(7), {
      isEditable: true,
      presentationRevision: 1,
      sheets: [],
      templateVersionId: 7,
    });

    expect(resolveCrumbValue(client, 'templateVersionLabel', { id: '1', versionId: '7' })).toEqual({
      status: 'resolved',
      text: '1.0',
    });
  });

  it('версії з іншим id у тому ж переліку — unavailable', () => {
    const client = new QueryClient();
    seedVersionsOf(client, 1);

    expect(resolveCrumbValue(client, 'templateVersionLabel', { id: '1', versionId: '999' })).toEqual(
      { status: 'unavailable' },
    );
  });

  it('без :versionId у параметрах — unavailable', () => {
    const client = new QueryClient();
    seedVersionsOf(client, 1);

    expect(resolveCrumbValue(client, 'templateVersionLabel', { id: '1' })).toEqual({
      status: 'unavailable',
    });
  });
});

describe('resolveCrumbValue — registryName', () => {
  it('резолвить локалізовану назву довідника з registries.definition(code)', () => {
    const client = new QueryClient();
    seedRegistryDefinition(client, 'EMISSIONS', 'Emissions Registry');

    expect(resolveCrumbValue(client, 'registryName', { code: 'EMISSIONS' })).toEqual({
      status: 'resolved',
      text: 'Emissions Registry',
    });
  });

  it('без :code у параметрах — unavailable', () => {
    const client = new QueryClient();

    expect(resolveCrumbValue(client, 'registryName', {})).toEqual({ status: 'unavailable' });
  });
});
