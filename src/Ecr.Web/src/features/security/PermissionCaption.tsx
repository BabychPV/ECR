import type { JSX } from 'react';
import { TwoLine } from '@/shared/ui/TwoLine';
import { permissionLabel } from './permissionLabel';

/**
 * Підпис права: назва зверху, код приглушено під нею (`U-11`).
 *
 * ⚠ Окремий компонент, а не два виклики `TwoLine` на місці: те саме право
 * підписується у ДВОХ місцях одного екрана — заголовком колонки матриці й
 * прапорцем у формі створення ролі. Два незалежні підписи розійшлися б рівно
 * так, як уже розходилися підписи статусів (`#440`: у таблиці — назва, у
 * випадному списку — код сервера).
 *
 * ⛔ Другий рядок не малюється, коли він дослівно дорівнює першому. Так буває
 * саме в запасному варіанті — право, якого ще немає в каталозі рядків, і
 * `permissionLabel` віддає сам код. Показати той самий код двічі поспіль —
 * це не «код лишився доступним», це шум, і на 41 колонці він подвоївся б.
 */
export function PermissionCaption({ code }: { readonly code: string }): JSX.Element {
  const label = permissionLabel(code);

  return (
    <TwoLine
      primary={label}
      {...(label === code ? {} : { secondary: code })}
      mono
      title={code}
    />
  );
}
