import { useMutation, useQueryClient, type UseMutationResult } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { projectsQuery } from './allProjects';

/**
 * ✎ UAT 08.11 (c): видалення проєкту — `DELETE /api/v1/projects/{id}`. Сервер дозволяє лише ЧЕРНЕТКУ без документів
 * і без історії (грант Manage, запис у журналі змін); інакше — `409 ECR-PRD-0409` з ключем умови
 * (`deleteNotDraft` зі станом і кількістю документів, `deleteHasDocuments`, `deleteHasHistory`).
 *
 * ⚠ Кнопки на сторінці ЩЕ НЕМАЄ: вона чекає погодження макета людиною (сторінка проєкту, f/g). Тут — типізований
 * виклик і хук, з яких її збудують.
 */
export function deleteProject(projectId: number): Promise<void> {
  return apiFetch<void>(`/api/v1/projects/${String(projectId)}`, { method: 'DELETE' });
}

/** Видалення проєкту як мутація; після успіху перелік проєктів перечитується. */
export function useDeleteProject(onDeleted?: () => void): UseMutationResult<void, unknown, number> {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: deleteProject,
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: projectsQuery.queryKey });
      onDeleted?.();
    },
  });
}
