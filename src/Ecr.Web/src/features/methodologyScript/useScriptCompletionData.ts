import { useQueries, useQuery } from '@tanstack/react-query';
import { useMemo } from 'react';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import { templatesListQuery } from '@/features/templates/templateQueries';
import { methodologyScriptTables } from './api';
import { scriptKeys } from './scriptQueryKeys';
import type {
  TemplateStructureDto,
  TemplateVersionsForTemplate,
} from '@/api/types';
import { expressionMetadata } from '../expressions/api';
import type { ScriptCompletionBase } from './ScriptEditor';
import {
  assembleCompletionBase,
  inputTablesFromDtos,
  liveTemplateVersionId,
  scriptTableCodes,
  templatesToRead,
  type ScriptTemplateSource,
} from './scriptInputTables';
import type { ScriptOutlineDto } from './types';

const StaleMs = 5 * 60_000;

/**
 * Дані для підказок редактора:
 *  • колонки таблиць — одним запитом `GET /api/v1/methodologies/script/tables?codes=`;
 *  • ЗАПАСНИЙ шлях (лише коли ендпоінт нічого не віддав чи впав): перелік шаблонів → чинні версії → структура версії;
 *  • метадані виразів діалекту `Methodology` (вбудовані функції, поля шапки).
 * Усе читається лише коли скрипт уже називає таблиці.
 */
export function useScriptCompletionData(
  outline: ScriptOutlineDto | null,
  methodologyVersionId: number,
): ScriptCompletionBase {
  const wantsTables = (outline?.tables.length ?? 0) > 0;

  const tableCodes = useMemo(() => scriptTableCodes(outline), [outline]);
  const tableCodesKey = tableCodes.join(',');
  const liveTablesQuery = useQuery({
    queryKey: scriptKeys.tables(tableCodesKey),
    queryFn: () => methodologyScriptTables(tableCodes),
    enabled: tableCodes.length > 0,
    staleTime: StaleMs,
  });
  const liveTables = useMemo(
    () => inputTablesFromDtos(liveTablesQuery.data ?? []),
    [liveTablesQuery.data],
  );
  // Запасний шлях вмикається, лише коли ендпоінт відповів порожньо або помилкою.
  const needsFallback =
    wantsTables &&
    (liveTablesQuery.isError || (liveTablesQuery.isSuccess && liveTables.length === 0));

  // ⛔ PSS-19c: усі шаблони (`templatesListQuery`): таблиця 101-го шаблону інакше не мала б підказок.
  const templates = useQuery({ ...templatesListQuery, enabled: needsFallback, staleTime: StaleMs });

  const toRead = useMemo(
    () => (needsFallback ? templatesToRead(outline, templates.data?.items ?? []) : []),
    [outline, templates.data, needsFallback],
  );
  const ids = useMemo(() => toRead.map((template) => template.id), [toRead]);

  const versions = useQuery({
    queryKey: queryKeys.templates.versionsBatch(ids),
    queryFn: () =>
      apiFetch<TemplateVersionsForTemplate[]>(
        `/api/v1/templates/versions?${ids.map((id) => `ids=${String(id)}`).join('&')}`,
      ),
    enabled: needsFallback && ids.length > 0,
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
        liveTables,
      }),
    [outline, sources, functions, headers, liveTables],
  );
}
