import { apiFetch } from '@/api/client';
import type {
  CalculationLevel,
  CreateMethodologyVersionRequest,
  MethodologyDraftVersionDto,
  MethodologyFormulaDto,
  SaveMethodologyFormulaRequest,
} from '@/api/types';
import { formulaBody, type FormulaDraft } from './draft';

/**
 * Звернення конфігуратора методологій (`ФВ-9.15`).
 *
 * ⛔ Читання тут **не те саме**, що `GET /api/v1/methodologies`. Той перелік
 * віддає лише опубліковані версії — те, чим рахують. Конфігуратор питає про
 * те, що правлять, а правити можна лише чернетку, тож йому потрібні і вона
 * теж. Спроба обійтися одним переліком закінчилася б тим, що кнопка
 * «Опублікувати» стоїть для версій, яких у переліку немає за побудовою.
 */

/** Усі версії методології, включно з чернетками. */
export function methodologyVersions(
  methodologyId: number,
): Promise<MethodologyDraftVersionDto[]> {
  return apiFetch<MethodologyDraftVersionDto[]>(
    `/api/v1/methodologies/${String(methodologyId)}/versions`,
  );
}

/** Формули версії в порядку обчислення. */
export function methodologyFormulas(
  methodologyId: number,
  versionId: number,
): Promise<MethodologyFormulaDto[]> {
  return apiFetch<MethodologyFormulaDto[]>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/formulas`,
  );
}

/**
 * Створює версію-чернетку: клон наявної або порожню.
 *
 * ⛔ Клон — **єдиний спосіб змінити опубліковану версію** (`ФВ-9.1`): вона
 * незмінна, бо на її числа посилаються вже подані форми.
 */
export function createMethodologyVersion(
  methodologyId: number,
  body: {
    readonly versionNumber: string;
    readonly copyFromVersionId: number | null;
    readonly level: CalculationLevel;
  },
): Promise<MethodologyDraftVersionDto> {
  return apiFetch<MethodologyDraftVersionDto>(
    `/api/v1/methodologies/${String(methodologyId)}/versions`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        versionNumber: body.versionNumber,
        copyFromVersionId: body.copyFromVersionId,
        level: body.level,
      } satisfies CreateMethodologyVersionRequest),
    },
  );
}

/**
 * Записує формулу чернетки; створює її, якщо коду ще немає.
 *
 * ⚠ `PUT`, а не `POST`: адресою формули є її **код** у межах версії — те, чим
 * на неї посилаються вирази (`!Name`). Тому створення й зміна — одна дія, і
 * повторний запит із тим самим тілом дає той самий стан.
 *
 * ⚠ Код іде через `encodeURIComponent`: у ньому дозволені лише латиниця,
 * цифри й підкреслення (`EcrCode`), але покладатися на це в побудові адреси
 * означало б, що перша ж послаблена перевірка коду ламає маршрутизацію мовчки.
 */
export function saveMethodologyFormula(
  methodologyId: number,
  draft: FormulaDraft,
): Promise<MethodologyFormulaDto> {
  const body: SaveMethodologyFormulaRequest = formulaBody(draft);

  return apiFetch<MethodologyFormulaDto>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(draft.versionId)}/formulas/${encodeURIComponent(draft.code)}`,
    {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    },
  );
}

/** Прибирає формулу з чернетки. */
export function deleteMethodologyFormula(
  methodologyId: number,
  versionId: number,
  code: string,
): Promise<void> {
  return apiFetch<void>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/formulas/${encodeURIComponent(code)}`,
    { method: 'DELETE' },
  );
}
