import { lazy, Suspense, type JSX } from 'react';
import { inProjects, isRecalcActive, isRecentlyFinished } from './recalcProgress';
import { useProjectRecalcProgress } from './useProjectRecalcProgress';

/** Розмітка банера — окремим чанком (`D-132`): вантажиться лише тоді, коли є що показати. */
const ProjectRecalcBanner = lazy(async () => ({
  default: (await import('./ProjectRecalcBanner')).ProjectRecalcBanner,
}));

/**
 * ✎ UAT recalc-progress (08.11): місце банера прогресу перерахунку на сторінці. Сам запит стану — статичний і
 * дешевий (той самий ключ кешу, що й у банера), а розмітка банера вантажиться за `import()` лише коли перерахунок
 * іде або щойно завершився.
 *
 * ⚠ Без цього сторінка (і кожен її тест) щоразу тягнула б чанк банера, навіть коли перерахунків немає; у тестах
 * він доїжджав уже після демонтажу середовища («Vite module runner has been closed»).
 */
export function ProjectRecalcBannerSlot({
  projectId,
  onlyProjects,
}: {
  readonly projectId: number | null;
  /** Див. `ProjectRecalcBanner.onlyProjects`; без нього — як і раніше, усі рядки запиту. */
  readonly onlyProjects?: readonly number[] | undefined;
}): JSX.Element | null {
  const { items } = useProjectRecalcProgress(projectId);
  const now = Date.now();

  if (!inProjects(items, onlyProjects).some((item) => isRecalcActive(item) || isRecentlyFinished(item, now))) {
    return null;
  }

  return (
    <Suspense fallback={null}>
      <ProjectRecalcBanner projectId={projectId} onlyProjects={onlyProjects} />
    </Suspense>
  );
}
