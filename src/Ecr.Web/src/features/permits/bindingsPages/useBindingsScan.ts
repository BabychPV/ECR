import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { TemplatePage, TemplateVersionsForTemplate, TemplateVersionSummary } from '@/api/types';
import { listPermitBindings } from '../api/bindingsApi';
import {
  buildScanPlan,
  collectOutcome,
  ScanConcurrency,
  type ScanOutcome,
  type ScanPlan,
  type ScanResult,
  type ScanSource,
} from './scanModel';

/** Скільки секунд відповідь про прив'язки версії вважається свіжою: повернення на екран не перечитує все наново. */
const ScanStaleMs = 30_000;

/** Ключ кешу прив'язок однієї версії. */
export const scanKey = (versionId: number) => ['permits', 'bindingsScan', versionId] as const;

/** Пакетне читання версій шаблонів: той самий запит і ключ, що й у переліку `/admin/templates`. */
export function fetchVersionsBatch(templateIds: readonly number[]): Promise<readonly TemplateVersionsForTemplate[]> {
  const idsQuery = templateIds.map((id) => `ids=${String(id)}`).join('&');

  return apiFetch<readonly TemplateVersionsForTemplate[]>(`/api/v1/templates/versions?${idsQuery}`);
}

export interface BindingsScan {
  /** Шаблони чи їхні версії ще вантажаться (перший етап). */
  readonly planning: boolean;
  /** Помилка першого етапу (перелік шаблонів чи їхніх версій) - її на рівні рядка не показати. */
  readonly planError: unknown;
  readonly plan: ScanPlan | undefined;
  readonly outcome: ScanOutcome;
  /** Сервер віддав не всі шаблони (є `nextCursor`): перелік охоплює лише першу сторінку. */
  readonly templatesTruncated: boolean;
  /** Повторити читання версій, що впали. */
  readonly retryFailed: () => void;
  /** Повторити перший етап. */
  readonly retryPlan: () => void;
}

/**
 * Збирає прив'язки з усіх версій першої порції шаблонів.
 *
 * ⚠ Паралельність обмежена `ScanConcurrency`: пул із фіксованої кількості «робітників» бере наступну версію,
 * щойно попередня відповіла, тож сторінка ніколи не тримає більше ніж 4 запитів у польоті, хоч би скільки було
 * шаблонів. Помилка однієї версії лишається в її результаті й не зупиняє решту.
 * ⚠ Відповіді кешуються через `fetchQuery` (ключ — версія): повторний вхід у `staleTime` не б'є в мережу.
 */
export function useBindingsScan(enabled: boolean, limit: number): BindingsScan {
  const client = useQueryClient();

  const templates = useQuery({
    queryKey: queryKeys.templates.list(),
    queryFn: () => apiFetch<TemplatePage>('/api/v1/templates?limit=100'),
    enabled,
  });
  const templateIds = useMemo(() => (templates.data?.items ?? []).map((template) => template.id), [templates.data]);
  const versions = useQuery({
    queryKey: queryKeys.templates.versionsBatch(templateIds),
    queryFn: () => fetchVersionsBatch(templateIds),
    enabled: enabled && templateIds.length > 0,
  });

  const plan = useMemo<ScanPlan | undefined>(() => {
    if (templates.data === undefined) return undefined;
    if (templates.data.items.length > 0 && versions.data === undefined) return undefined;

    const byTemplate = new Map<number, readonly TemplateVersionSummary[]>(
      (versions.data ?? []).map((entry) => [entry.templateId, entry.versions]),
    );

    return buildScanPlan(templates.data.items, byTemplate, limit);
  }, [templates.data, versions.data, limit]);

  const [results, setResults] = useState<ReadonlyMap<string, ScanResult>>(new Map());
  // Версії, які пул уже взяв у роботу (щоб не читати двічі при зміні порції); пишеться лише в ефекті й обробнику.
  const claimed = useRef(new Set<string>());
  const [round, setRound] = useState(0);

  const sources = plan?.sources;
  useEffect(() => {
    if (sources === undefined) return undefined;

    let cancelled = false;
    const todo: ScanSource[] = sources.filter((source) => !claimed.current.has(source.key));
    let next = 0;

    async function worker(): Promise<void> {
      while (!cancelled) {
        const source = todo[next];
        next += 1;
        if (source === undefined) return;

        claimed.current.add(source.key);
        let result: ScanResult;
        try {
          const data = await client.fetchQuery({
            queryKey: scanKey(source.versionId),
            queryFn: () => listPermitBindings(source.versionId),
            staleTime: ScanStaleMs,
          });
          result = { status: 'success', data };
        } catch (error) {
          result = { status: 'error', error };
        }

        // Результат кладеться й після скасування: версію вже взято в роботу, і новий запуск її не повторить.
        setResults((previous) => new Map(previous).set(source.key, result));
      }
    }

    void Promise.all(Array.from({ length: Math.min(ScanConcurrency, todo.length) }, () => worker()));

    return () => {
      cancelled = true;
    };
  }, [sources, client, round]);

  const outcome = useMemo(() => collectOutcome(sources ?? [], results), [sources, results]);

  const retryFailed = useCallback(() => {
    const failedKeys = [...results].filter(([, result]) => result.status === 'error').map(([key]) => key);
    for (const key of failedKeys) {
      claimed.current.delete(key);
      client.removeQueries({ queryKey: scanKey(Number(key)) });
    }
    setResults((previous) => {
      const copy = new Map(previous);
      for (const key of failedKeys) copy.delete(key);

      return copy;
    });
    setRound((value) => value + 1);
  }, [results, client]);

  const retryPlan = useCallback(() => {
    void templates.refetch();
    void versions.refetch();
  }, [templates, versions]);

  const planError = templates.error ?? versions.error;

  return {
    planning: enabled && plan === undefined && planError === null,
    planError,
    plan,
    outcome,
    templatesTruncated: templates.data?.nextCursor != null,
    retryFailed,
    retryPlan,
  };
}
