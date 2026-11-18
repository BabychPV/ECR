import { useQueries, useQuery } from '@tanstack/react-query';
import { useMemo } from 'react';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import { scriptKeys } from './scriptQueryKeys';
import type {
  TemplatePage,
  TemplateStructureDto,
  TemplateVersionsForTemplate,
} from '@/api/types';
import { expressionMetadata } from '../expressions/api';
import type { ScriptCompletionBase } from './ScriptEditor';
import {
  assembleCompletionBase,
  liveTemplateVersionId,
  templatesToRead,
  type ScriptTemplateSource,
} from './scriptInputTables';
import type { ScriptOutlineDto } from './types';

const StaleMs = 5 * 60_000;

/**
 * Дані для підказок редактора з НАЯВНИХ API (нового ендпоінта «колонки за кодом
 * таблиці» немає):
 *  • перелік шаблонів → чинні версії → структура версії (колонки таблиць);
 *  • метадані виразів діалекту `Methodology` (вбудовані функції, поля шапки).
 * Структури читаються лише коли скрипт уже називає таблиці, і лише для шаблонів,
 * які він називає (`Шаблон.Таблиця`), а без назви — для всіх неархівних.
 */
export function useScriptCompletionData(
  outline: ScriptOutlineDto | null,
  methodologyVersionId: number,
): ScriptCompletionBase {
  const wantsTables = (outline?.tables.length ?? 0) > 0;

  const templates = useQuery({
    queryKey: queryKeys.templates.list(),
    queryFn: () => apiFetch<TemplatePage>('/api/v1/templates?limit=100'),
    enabled: wantsTables,
    staleTime: StaleMs,
  });

  const toRead = useMemo(
    () => templatesToRead(outline, templates.data?.items ?? []),
    [outline, templates.data],
  );
  const ids = useMemo(() => toRead.map((template) => template.id), [toRead]);

  const versions = useQuery({
    queryKey: queryKeys.templates.versionsBatch(ids),
    queryFn: () =>
      apiFetch<TemplateVersionsForTemplate[]>(
        `/api/v1/templates/versions?${ids.map((id) => `ids=${String(id)}`).join('&')}`,
      ),
    enabled: ids.length > 0,
    staleTime: StaleMs,
  });

  const live = useMemo(() => {
    const result: { templateCode: string; versionId: number }[] = [];
    for (const group of versions.data ?? []) {
      const template = toRead.find((candidate) => candidate.id === group.templateId);
      const versionId = liveTemplateVersionId(group.versions);
      if (template !== undefined && versionId !== null) {
        result.push({ templateCode: template.code, versionId });
      }
    }
    return result;
  }, [versions.data, toRead]);

  const structures = useQueries({
    queries: live.map((entry) => ({
      queryKey: queryKeys.templates.version(entry.versionId),
      queryFn: () =>
        apiFetch<TemplateStructureDto>(
          `/api/v1/template-versions/${String(entry.versionId)}/structure`,
        ),
      staleTime: StaleMs,
    })),
  });

  const metadata = useQuery({
    queryKey: scriptKeys.metadata(methodologyVersionId),
    queryFn: () => expressionMetadata('Methodology', { methodologyVersionId }),
    staleTime: StaleMs,
  });

  const sources = useMemo<ScriptTemplateSource[]>(() => {
    const result: ScriptTemplateSource[] = [];
    structures.forEach((query, index) => {
      const entry = live[index];
      if (entry !== undefined && query.data !== undefined) {
        result.push({ templateCode: entry.templateCode, structure: query.data });
      }
    });
    return result;
    // `structures` — новий масив на кожен рендер; залежимо від його даних.
  }, [live, ...structures.map((query) => query.data)]);

  const functions = metadata.data?.functions;
  const headers = metadata.data?.headers;

  return useMemo(
    () =>
      assembleCompletionBase({
        outline,
        sources,
        functions: functions ?? [],
        headerFields: (headers ?? []).map((header) => header.name),
      }),
    [outline, sources, functions, headers],
  );
}
