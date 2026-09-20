/**
 * Точка входу набору `Wizard` (директива №15 §2, шар 3).
 *
 * ⚠ Тека, а не один файл: майстер ще обросте кроками загального призначення
 * (вибір файлу для імпорту, вибір періоду), і кожен такий крок — новий файл
 * ПОРУЧ, а не ще сотня рядків у `Wizard.tsx` (CLAUDE.md §2: «нова
 * функціональність — у нових файлах, не в наявних великих»).
 */
export {
  Wizard,
  WizardReviewStepId,
  type WizardApi,
  type WizardExit,
  type WizardLabels,
  type WizardProps,
  type WizardStep,
  type WizardStepError,
  type WizardStepRenderArgs,
  type WizardStepValidation,
} from './Wizard';
