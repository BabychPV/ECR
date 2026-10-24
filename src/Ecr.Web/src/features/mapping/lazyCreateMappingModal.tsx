import { Suspense, lazy, type ComponentProps, type JSX } from 'react';
import type { CreateMappingModal } from './CreateMappingModal';
import { useRestoreFocus } from '@/shared/ui/useRestoreFocus';

/*
 * «Add mapping» на сторінці перегляду мапінгу — лінивим чанком (D-132, бюджет маршруту `MappingPreviewPage`): форма
 * тягне `NumberInput` (`react-number-format`), каталог PI AF і пробу джерела, а потрібна лише після кліку.
 *
 * ⚠ Обгортка змонтована ЗАВЖДИ (там само, де був статичний діалог): закрита Mantine `Modal` нічого не малює, тож
 * розмітка сторінки та сама, а чанк вантажиться разом зі сторінкою.
 *
 * ⛔ WCAG 2.4.3: кнопку «Add mapping» запам'ятовує `useRestoreFocus` ОБГОРТКИ. `returnFocus` Mantine ловить відкривача
 * лише на переході `opened` false → true усередині змонтованої `Modal`; клік раніше, ніж приїхав чанк, монтує діалог
 * уже відкритим, і після закриття фокус падав на `BODY`. Коли чанк уже є, обидва повертають на ту саму кнопку.
 *
 * ⚠ `EntityPipeline` імпортує `CreateMappingModal` статично — він і сам лінивий чанк, тож тут нічого не змінюється.
 */
const CreateMappingModalChunk = lazy(async () => ({
  default: (await import('./CreateMappingModal')).CreateMappingModal,
}));

/** `CreateMappingModal` лінивим чанком. */
export function LazyCreateMappingModal(props: ComponentProps<typeof CreateMappingModal>): JSX.Element {
  useRestoreFocus(props.opened);

  return (
    <Suspense fallback={null}>
      <CreateMappingModalChunk {...props} />
    </Suspense>
  );
}
