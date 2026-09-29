import type { JSX } from 'react';
import { Badge, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { EcrApiError, apiFetch } from '@/api/client';
import type { GrantableProject } from '@/api/types';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';

/**
 * Область дії призначення ролі за проєктами (ФВ-6.14) — спільне для форми
 * ролей користувача й групових призначень.
 *
 * ⚠ Порожній перелік тут — «усі проєкти» (на сервер області тоді не шлемо).
 * Сервер порожньої області не приймає (`422`): роль, що не діє ніде, —
 * не те, що адміністратор мав на увазі, лишивши поле порожнім.
 */

/** Довідник проєктів для вибору області — той самий, що й у гранті (D-207 п.2). */
export function useGrantableProjects(enabled: boolean): {
  readonly projects: readonly GrantableProject[];
  readonly failed: boolean;
} {
  // ⚠ Ключ і форма відповіді — ті самі, що в `ResourcePicker`: розбіжні ключі
  // для тих самих даних — відомий клас дефекту.
  const query = useQuery({
    queryKey: ['security', 'projects'],
    queryFn: () => apiFetch<GrantableProject[]>('/api/v1/security/projects'),
    enabled,
  });

  const projects = Array.isArray(query.data)
    ? query.data.filter(
        (p): p is GrantableProject =>
          typeof p === 'object' && p !== null && typeof p.id === 'number' && typeof p.code === 'string',
      )
    : [];

  return { projects, failed: query.isError };
}

function projectLabel(project: GrantableProject): string {
  const name = localized(project.nameL10n);
  return name.length > 0 && name !== project.code ? `${name} (${project.code})` : project.code;
}

/**
 * Варіанти мультивибору проєктів.
 *
 * ⚠ Проєкт області, якого немає в довіднику (довідник не завантажився або
 * права на нього немає), лишається обраним під своїм номером: зникнути з поля
 * він не має права — збереження тоді мовчки звузило б область.
 */
export function projectOptions(
  projects: readonly GrantableProject[],
  selectedIds: readonly number[],
): { value: string; label: string }[] {
  const known = projects.map((p) => ({ value: String(p.id), label: projectLabel(p) }));
  const missing = selectedIds
    .filter((id) => !projects.some((p) => p.id === id))
    .map((id) => ({ value: String(id), label: `#${String(id)}` }));

  return [...known, ...missing];
}

/** Область у рядку таблиці: назви проєктів або «усі проєкти». */
export function ScopeSummary({
  projectIds,
  projects,
}: {
  projectIds: readonly number[] | null | undefined;
  projects: readonly GrantableProject[];
}): JSX.Element {
  if (projectIds === null || projectIds === undefined) {
    return (
      <Badge variant="light" color="gray" data-scope="all">
        {t('security.scopeAllProjects')}
      </Badge>
    );
  }

  // ⛔ Порожній перелік від сервера — область не розібралася, роль не діє
  // НІДЕ. «Усі проєкти» тут було б неправдою в бік ширших прав.
  if (projectIds.length === 0) {
    return (
      <Text size="sm" c="statusWarning" data-scope="nowhere">
        {t('security.scopeNowhere')}
      </Text>
    );
  }

  return (
    <Text size="sm" data-scope="projects">
      {projectOptions(projects, projectIds)
        .filter((option) => projectIds.includes(Number(option.value)))
        .map((option) => option.label)
        .join(', ')}
    </Text>
  );
}

/**
 * ⛔ Роль з областю не дає ГЛОБАЛЬНИХ прав (`Security.*`, `Template.*`,
 * `Registry.*` тощо) — вони не прив'язані до проєкту, і сервер їх із такої
 * ролі не бере. Без попередження адміністратор обмежував би роль проєктом і
 * дивувався, куди зникло керування шаблонами.
 */
export function ScopeGlobalWarning(): JSX.Element {
  return (
    <Text size="sm" c="statusWarning" mt="xs" data-scope-warning>
      {t('security.scopeGlobalWarning')}
    </Text>
  );
}

/**
 * Відмова сервера про область: `403 noProjectManageGrant` (немає `Manage` на
 * проєкт) або `422` з кодом ролі. `null` — відмова про інше.
 */
export function scopeProblem(error: unknown): { roleCode: string | null; projectId: number | null } | null {
  if (!(error instanceof EcrApiError)) return null;

  const ext = error.problem.extensions2 ?? {};
  const projectId = ext['projectId'] === undefined ? null : Number(ext['projectId']);
  const roleCode = typeof ext['code'] === 'string' ? ext['code'] : null;

  if (error.problem.status === 403 && ext['messageKey'] === 'err.ECR-AUTH-0403.noProjectManageGrant') {
    return { roleCode, projectId };
  }

  if (error.problem.status === 422 && roleCode !== null) {
    return { roleCode, projectId };
  }

  return null;
}
