import type { JSX } from 'react';
import { useProjectTemplateApproval } from '@/features/approvalRoutes/ProjectTemplateApproval';
import { ProjectTemplatesSection } from './ProjectTemplatesSection';

/**
 * ✎ 24.11 (рішення людини «Одна таблиця шаблонів»): вкладка проєкту Templates — маршрут погодження проєкту одним
 * рядком і ОДНА таблиця шаблонів (версія, документи, погодження, «Edit exception», «Detach»), а під нею
 * «Add template». Раніше те саме було двома майже однаковими таблицями на одній вкладці Settings.
 *
 * ⚠ `readOnly` — як і раніше для «Add template»/«Detach» (архів або немає `Project.Manage` + гранта Manage);
 * `managesProject` — погодження (рядок маршруту, колонка, виняток), як раніше розділи «Approval route» і
 * «Templates: approval».
 */
export function ProjectTemplatesTab({
  projectId,
  readOnly,
  managesProject,
}: {
  readonly projectId: number;
  readonly readOnly: boolean;
  readonly managesProject: boolean;
}): JSX.Element {
  const approval = useProjectTemplateApproval(projectId, managesProject);

  return (
    <ProjectTemplatesSection projectId={projectId} readOnly={readOnly} toolbar={approval.toolbar} extension={approval.extension} />
  );
}
