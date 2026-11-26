import { useMutation, useQuery, useQueryClient, type UseMutationResult } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { ProjectPurgePreviewDto, PurgeProjectRequest } from '@/api/types';
import { projectsQuery } from './allProjects';

/**
 * ✎ 25.11: видалення проєкту З ДАНИМИ (людина: видалити активні проєкти через інтерфейс).
 *
 * - `GET /api/v1/projects/{id}/deletion-preview` — що зникне (документи з ключами, кількості, місяці) і що заважає
 *   (`blocker`: архів B69 або залежні документи інших проєктів B80);
 * - `POST /api/v1/projects/{id}/purge` — набраний код і обов'язкова причина; право `Project.DeleteWithData`.
 *
 * Споживач — `PurgeProjectModal` (вкладка Lifecycle проєкту).
 */
export function purgePreviewQuery(projectId: number, enabled: boolean) {
  return {
    queryKey: ['projects', projectId, 'deletion-preview'] as const,
    queryFn: () => apiFetch<ProjectPurgePreviewDto>(`/api/v1/projects/${String(projectId)}/deletion-preview`),
    enabled,
    // ⚠ Кількості мають бути свіжими на момент відкриття діалогу.
    staleTime: 0,
    gcTime: 0,
  };
}

export function usePurgePreview(projectId: number, enabled: boolean) {
  return useQuery(purgePreviewQuery(projectId, enabled));
}

export function purgeProject(projectId: number, request: PurgeProjectRequest): Promise<void> {
  return apiFetch<void>(`/api/v1/projects/${String(projectId)}/purge`, {
    method: 'POST',
    body: JSON.stringify(request),
  });
}

/** Мутація видалення; після успіху перелік проєктів перечитується. Відмову показує споживач (`meta.handled`). */
export function usePurgeProject(
  projectId: number,
  onDeleted?: () => void,
): UseMutationResult<void, unknown, PurgeProjectRequest> {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (request: PurgeProjectRequest) => purgeProject(projectId, request),
    meta: { handled: true },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: projectsQuery.queryKey });
      onDeleted?.();
    },
  });
}
