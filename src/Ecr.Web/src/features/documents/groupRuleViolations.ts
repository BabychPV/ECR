import type { TemplateStructureDto } from '@/api/types';

/**
 * Рівно те, що читає перевірка: аркуш — ідентифікатор і група. ⚠ Не вся
 * структура: діалог створення документа бере аркуші з
 * `GET /projects/{id}/document-template` (V-12), де таблиць немає.
 */
type Composition = {
  sheets: readonly Pick<TemplateStructureDto['sheets'][number], 'id' | 'sheetGroup'>[];
  groupRules: TemplateStructureDto['groupRules'];
};
import { t } from '@/shared/i18n';

/** Правило «усі аркуші групи обов'язкові» — той самий код, що на сервері. */
const RequiresAll = 0;

/** Правило «хоча б один аркуш групи» — той самий код, що на сервері. */
const RequiresOne = 1;

/**
 * Live-попередження про порушення `SheetGroupRule` (директива "live-
 * попередження про порушення SheetGroupRule", `ФВ-3.2`).
 *
 * ⛔ До цього порушення складу дізнавалися лише після кліку «Save» —
 * `CreateDocumentModal.tsx` не мав жодної клієнтської перевірки, і сервер
 * (`DocumentStore.ValidateCompositionAsync`) був єдиним, хто це рахував.
 *
 * ⚠ Навмисно дзеркалить РІВНО ту саму пару правил, що сервер справді
 * перевіряє сьогодні — `RequiresAll` (0) і `RequiresOne` (1).
 * `Excludes` (2) сервер не перевіряє (`DocumentStore.ValidateCompositionAsync`
 * не має гілки для нього), і попереджати про правило, яке сервер не
 * застосовує, означало б розійтися з ним, а не підстрахувати його: клієнт
 * заблокував би комбінацію, яку сервер прийняв би без жодних заперечень.
 *
 * ⚠ Сервер лишається останньою лінією правди навмисно (кнопка «Save» тут не
 * блокується) — про всяк випадок, якщо ця копія колись розійдеться з
 * оригіналом.
 */
export function groupRuleViolations(
  structure: Composition | undefined,
  selectedSheetIds: readonly number[],
): string[] {
  if (structure === undefined) {
    return [];
  }

  const chosen = new Set(selectedSheetIds);
  const messages: string[] = [];

  for (const rule of structure.groupRules) {
    const inGroup = structure.sheets.filter((sheet) => sheet.sheetGroup === rule.sheetGroup);
    if (inGroup.length === 0) {
      continue;
    }

    const picked = inGroup.filter((sheet) => chosen.has(sheet.id)).length;

    if (rule.ruleKind === RequiresAll && picked > 0 && picked < inGroup.length) {
      messages.push(
        t('documents.groupRuleRequiresAll', { group: rule.sheetGroup, picked, total: inGroup.length }),
      );
    }

    if (rule.ruleKind === RequiresOne && picked === 0) {
      messages.push(t('documents.groupRuleRequiresOne', { group: rule.sheetGroup }));
    }
  }

  return messages;
}
