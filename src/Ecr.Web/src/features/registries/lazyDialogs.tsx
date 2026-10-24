import { Suspense, lazy, type ComponentProps, type JSX } from 'react';
import type { CreateRegistryModal } from './CreateRegistryModal';
import type { DeleteEntryDialog } from './DeleteEntryDialog';
import type { RegistryEntryEditor, ValidityEditor } from './RegistryEntryEditor';

/*
 * Діалоги сторінки довідників — лінивими чанками (D-132, бюджет маршруту `RegistriesPage`).
 *
 * ⚠ Кожна обгортка змонтована ЗАВЖДИ, як і статичний діалог до неї: у закритому стані Mantine
 * `Modal` нічого не малює, тож розмітка сторінки та сама, чанк вантажиться разом зі сторінкою, а
 * повернення фокуса на кнопку, що відкрила діалог, працює (Mantine запам'ятовує її на переході
 * `opened` false → true, якого в діалога, змонтованого вже відкритим, не було б).
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
  return (
    <Suspense fallback={null}>
      <CreateRegistryModalChunk {...props} />
    </Suspense>
  );
}

/** `RegistryEntryEditor` лінивим чанком. */
export function LazyRegistryEntryEditor(props: ComponentProps<typeof RegistryEntryEditor>): JSX.Element {
  return (
    <Suspense fallback={null}>
      <RegistryEntryEditorChunk {...props} />
    </Suspense>
  );
}

/** `ValidityEditor` лінивим чанком. */
export function LazyValidityEditor(props: ComponentProps<typeof ValidityEditor>): JSX.Element {
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
  return (
    <Suspense fallback={null}>
      <DeleteEntryDialogChunk {...props} />
    </Suspense>
  );
}
