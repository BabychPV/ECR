import { lazy, Suspense, useState, type JSX } from 'react';
import { Menu } from '@mantine/core';
import type { DocumentSummary } from '@/api/types';
import { localized } from '@/shared/i18n/localized';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { t } from '@/shared/i18n';

/**
 * ✎ b18 D-132: мутація й діалог підтвердження — окремий чанк (`DeleteDocumentHost`); у маршруті лишається пункт меню.
 * Вантажиться з першим відкриттям, на наведенні на пункт — заздалегідь.
 */
const loadDeleteDocumentHost = () => import('./DeleteDocumentHost');
const DeleteDocumentHost = lazy(loadDeleteDocumentHost);

/** Право, під яким сервер приймає `DELETE /api/v1/documents/{id}`. */
export const DeleteDocumentPermission = 'Document.Delete';

/**
 * Чи документ, НАСКІЛЬКИ ЙОГО ЗНАЄ КЛІЄНТ, — чернетка.
 *
 * ⚠ «Наскільки знає» — не застереження заради застереження. Сервер вважає
 * чернеткою документ, у якого ВСІ аркуші в `Draft` **і** немає жодного сліду
 * погодження (`DraftDocumentDeletion.EnsureDraft`). Другої половини клієнт не
 * бачить: відкликаний аркуш знову `Draft`, а історія лишилася. Тому `true`
 * тут означає «кнопку показати», а не «видалення пройде» — остаточну відповідь
 * дає сервер, і його відмову показуємо з причиною.
 *
 * ⛔ Аркуш, якого немає в `sheetStates`, — `Draft`: так само його читає й
 * сторінка документа (`?? 'Draft'`). Інакше документ, для якого сервер ще не
 * завів рядок стану, втратив би кнопку без жодної причини.
 */
export function isKnownDraft(
  sheetStates: Readonly<Record<string, string>>,
  sheetCodes: readonly string[],
): boolean {
  return (
    sheetCodes.every((code) => (sheetStates[code] ?? 'Draft') === 'Draft') &&
    Object.values(sheetStates).every((state) => state === 'Draft')
  );
}

interface DeleteDocumentActionArgs {
  readonly documentId: number;

  /** `undefined` — документ ще не приїхав: кнопки немає, бо назви немає. */
  readonly document: DocumentSummary | undefined;

  /** Коди аркушів документа за обраний період (з переліку таблиць). */
  readonly sheetCodes: readonly string[];

  /** Чи має сесія право `Document.Delete`. */
  readonly allowed: boolean;
}

interface DeleteDocumentAction {
  /**
   * Пункт меню «More» на сторінці документа; `null`, якщо дії немає.
   *
   * ⛔ Пункт меню, а не кнопка в рядку дій: рідкісна й незворотна дія не
   * живе серед щоденних — у рядку вона переповнювала його й «випадала» на
   * окремий рядок під поле періоду (знімок людини, 1290 px). Меню ж у рядку
   * займає рівно одне місце, хоч би що в ньому лежало.
   */
  readonly menuItem: JSX.Element | null;

  /**
   * Підтвердження. ⚠ ОКРЕМО від пункту: випадне меню розмонтовує свій вміст,
   * щойно закривається, а закривається воно саме кліком по пункту — діалог
   * усередині пункту зник би разом із меню.
   */
  readonly dialog: JSX.Element | null;

  /** Банер відмови сервера — під шапкою; `null`, якщо відмови не було. */
  readonly refusal: JSX.Element | null;
}

/**
 * Дія «Видалити документ-чернетку» на сторінці документа.
 *
 * ⚠ Хук повертає ТРИ вузли (пункт, діалог, відмова), а не один компонент, і це рішення про місце, не
 * про стиль. Пункт живе в меню «More» сторінки (`DocumentMoreMenu`), а
 * відмова — банер `ErrorAlert` із причиною, кодом і кореляцією, якому в ряду
 * кнопок не місце. Покласти банер у `ConfirmModal` не можна: його `text`
 * загорнутий у `<Text>` (`<p>`), а `Alert` — це `<div>`, тобто невалідна
 * вкладеність. Діалог після відмови закривається, банер лишається на сторінці,
 * доки людина не відкриє діалог знову.
 *
 * ⛔ Відмова показується ПРИЧИНОЮ сервера, а не «не вдалося»: `409
 * ECR-DOC-0409` каже, ЧОМУ документ не чернетка (`deleteNotDraft` з аркушем і
 * станом або `deleteHasHistory`), `403` — що немає гранта на запис у проєкт.
 * Текст приходить уже локалізованим (`messageKey`), його розбирає
 * `ErrorAlert` → `problemText` — єдине місце на весь застосунок.
 */
export function useDeleteDocumentAction({
  documentId,
  document,
  sheetCodes,
  allowed,
}: DeleteDocumentActionArgs): DeleteDocumentAction {
  const [opened, setOpened] = useState(false);
  const [everOpened, setEverOpened] = useState(false);
  if (opened && !everOpened) setEverOpened(true);
  const [error, setError] = useState<unknown>(null);

  const name =
    document === undefined
      ? ''
      : localized(document.nameL10n).length > 0
        ? `${localized(document.nameL10n)} · ${document.businessKey}`
        : document.businessKey;

  const shown =
    allowed && document !== undefined && isKnownDraft(document.sheetStates, sheetCodes);

  const menuItem = shown ? (
    <Menu.Item
      color="statusError"
      data-delete-document=""
      onClick={() => setOpened(true)}
      onPointerEnter={() => void loadDeleteDocumentHost()}
    >
      {t('documents.delete')}
    </Menu.Item>
  ) : null;

  const dialog =
    shown && everOpened ? (
      <Suspense fallback={null}>
        <DeleteDocumentHost
          documentId={documentId}
          name={name}
          opened={opened}
          onClose={() => setOpened(false)}
          onError={setError}
        />
      </Suspense>
    ) : null;

  // ⚠ Без `onRetry`: «повторити» тут означало б повторне видалення БЕЗ
  // підтвердження. Банер знімає наступне відкриття діалогу (скидання мутації в хості).
  const refusal = error === null ? null : <ErrorAlert error={error} />;

  return { menuItem, dialog, refusal };
}
