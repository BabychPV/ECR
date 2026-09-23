import { apiFetch } from '@/api/client';
import { headerFieldBody, type HeaderFieldDefDto, type HeaderFieldDraft } from './headerField';

/**
 * Звернення редактора полів шапки документа — за зразком `columnApi.ts`
 * (`W5.2`). На відміну від колонки, DELETE тут немає взагалі: сервер його
 * свідомо не оголошує (контракт серверної сесії, `docs/build/02-contracts.md`
 * §9) — поле шапки прибрати з інтерфейсу адміністратора не можна.
 */

/** Перелік полів шапки версії. Право `Template.View`. */
export function getHeaderFields(templateVersionId: number): Promise<HeaderFieldDefDto[]> {
  return apiFetch<HeaderFieldDefDto[]>(
    `/api/v1/template-versions/${String(templateVersionId)}/header-fields`,
  );
}

/**
 * Записує поле шапки; створює його, якщо коду ще немає.
 *
 * ⚠ `PUT`, а не `POST` — той самий draft→publish контракт, що `saveColumn`
 * (`columnApi.ts`).
 */
export function saveHeaderField(
  templateVersionId: number,
  draft: HeaderFieldDraft,
): Promise<HeaderFieldDefDto> {
  return apiFetch<HeaderFieldDefDto>(
    `/api/v1/template-versions/${String(templateVersionId)}/header-fields/${encodeURIComponent(draft.code)}`,
    {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(headerFieldBody(draft)),
    },
  );
}
