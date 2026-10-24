import { Suspense, lazy, type ComponentProps, type JSX } from 'react';
import type { CreateRegistryModal } from './CreateRegistryModal';
import type { DeleteEntryDialog } from './DeleteEntryDialog';
import type { RegistryEntryEditor, ValidityEditor } from './RegistryEntryEditor';
import { useRestoreFocus } from '@/shared/ui/useRestoreFocus';

/*
 * Діалоги сторінки довідників — лінивими чанками (D-132, бюджет маршруту `RegistriesPage`).
 *
 * ⚠ Кожна обгортка змонтована ЗАВЖДИ, як і статичний діалог до неї: у закритому стані Mantine
 * `Modal` нічого не малює, тож розмітка сторінки та сама, а чанк вантажиться разом зі сторінкою.
 *
 * ⛔ WCAG 2.4.3: відкривача запам'ятовує `useRestoreFocus` ОБГОРТКИ, а не лише `returnFocus` Mantine.
 * Той ловить кнопку на переході `opened` false → true усередині вже змонтованої `Modal`; натиснули
 * раніше, ніж приїхав чанк (мілісекунди після показу сторінки), — діалог монтується ВЖЕ відкритим,
 * переходу не було, і після закриття фокус падав на `BODY`. Обгортка ж змонтована завжди і бачить
 * перехід на коміті кліку, коли фокус ще на кнопці. Коли чанк уже є, обидва повертають на той самий
 * елемент — поведінка не змінюється. Виняток — `DeleteEntryDialog` (див. його обгортку).
 */
const CreateRegistryModalChunk = lazy(async () => ({
  default: (await import('./CreateRegistryModal')).CreateRegistryModal,
}));

const RegistryEntryEditorChunk = lazy(async () => ({
  default: (await import('./RegistryEntryEditor')).RegistryEntryEditor,
}));

const ValidityEditorChunk = lazy(async () => ({
  default: (await import('./RegistryEntryEditor')).ValidityEditor,
}));

/** `CreateRegistryModal` лінивим чанком. */
export function LazyCreateRegistryModal(props: ComponentProps<typeof CreateRegistryModal>): JSX.Element {
  useRestoreFocus(props.opened);

  return (
    <Suspense fallback={null}>
      <CreateRegistryModalChunk {...props} />
    </Suspense>
  );
}

/** `RegistryEntryEditor` лінивим чанком. */
export function LazyRegistryEntryEditor(props: ComponentProps<typeof RegistryEntryEditor>): JSX.Element {
  useRestoreFocus(props.opened);

  return (
    <Suspense fallback={null}>
      <RegistryEntryEditorChunk {...props} />
    </Suspense>
  );
}

/** `ValidityEditor` лінивим чанком. */
export function LazyValidityEditor(props: ComponentProps<typeof ValidityEditor>): JSX.Element {
  useRestoreFocus(props.entry !== null);

  return (
    <Suspense fallback={null}>
      <ValidityEditorChunk {...props} />
    </Suspense>
  );
}

const DeleteEntryDialogChunk = lazy(async () => ({
  default: (await import('./DeleteEntryDialog')).DeleteEntryDialog,
}));

/** `DeleteEntryDialog` лінивим чанком. */
export function LazyDeleteEntryDialog(props: ComponentProps<typeof DeleteEntryDialog>): JSX.Element {
  // ⚠ Без `useRestoreFocus` тут: `ConfirmModal` має власний і запам'ятовує відкривача на першому відкритому коміті, тобто
  // коли приїхав чанк, — кнопка «Delete» рядка на той час досі у фокусі (`lazyDialogs.focus.test.tsx`).
  return (
    <Suspense fallback={null}>
      <DeleteEntryDialogChunk {...props} />
    </Suspense>
  );
}
