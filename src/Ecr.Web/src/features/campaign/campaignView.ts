import type { CampaignProject } from '@/features/campaign/api';

/**
 * Хто затримує кампанію: проєкт, у якому звітність періоду ще не дійшла до
 * кінця.
 *
 * ⚠ Кінець кампанії — не «усе затверджено», а «є зріз»: документи можуть бути
 * затверджені, але доки поточного зрізу немає, регулятор не отримав нічого
 * (`CampaignProjectSummary.Snapshots`, `BE-22`). Тому проєкт затримує кампанію,
 * якщо виконується хоч одне:
 *   - документів немає зовсім — кампанія в ньому ще не починалася;
 *   - затверджено не все;
 *   - зрізу немає.
 */
export function isLagging(project: CampaignProject): boolean {
  return project.documents === 0 || project.approved < project.documents || project.snapshots === 0;
}

/** Лічильники станів документів, складені по проєктах. */
export interface CampaignTotals {
  readonly draft: number;
  readonly submitted: number;
  readonly approved: number;
  readonly rejected: number;
}

/**
 * Сума лічильників станів по ВСІХ переданих проєктах — і тих, що затримують,
 * і тих, що вже завершили.
 *
 * ⛔ Сумується саме те, що приїхало. Якщо сервер обрізав перелік стелею, це
 * сума по показаних проєктах, а не по кампанії, — і екран мусить сказати це
 * поруч (`isCampaignTruncated`), а не видавати її за повну.
 */
export function campaignTotals(projects: readonly CampaignProject[]): CampaignTotals {
  return projects.reduce<CampaignTotals>(
    (sum, project) => ({
      draft: sum.draft + project.draft,
      submitted: sum.submitted + project.submitted,
      approved: sum.approved + project.approved,
      rejected: sum.rejected + project.rejected,
    }),
    { draft: 0, submitted: 0, approved: 0, rejected: 0 },
  );
}
