import type { JSX } from 'react';
import { Button, Stack } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { MappedFieldPreview } from '@/api/types';
import { pauseEntityFieldMap, resumeEntityFieldMap } from './api';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/**
 * Пауза/відновлення мапінгу з рядка перегляду (`BE-27`).
 *
 * ⚠ Мутація ВЛАСНА для кожного рядка — компонент, викликаний як `<PauseResumeAction
 * field={field} />`, а не інлайн-функція в `.map`, тобто React тримає для
 * нього окремий стан хука. Інакше «завантаження» й відмова одного рядка
 * читалися б для всього переліку одразу — той самий клас дефекту, що аудит
 * 2026-09-16 §10.8 знайшов у `SourcesPage.tsx` (там лишили ОДНУ мутацію на
 * сторінку і розрізняють рядки через `variables`; тут рядків мало, і власна
 * мутація на кожен — простіше й не потребує того обхідного порівняння).
 *
 * ⛔ Кнопка ОДНА: яка саме — вирішує `field.isActive`, а не окремий
 * локальний прапорець. Мапінг не може бути одночасно «увімкнути» й
 * «вимкнути» для того самого рядка.
 *
 * ⚠ Без підтвердження. У макеті (`docs/design/hybrid/screens-data.js`) пауза
 * настає лише АВТОМАТИЧНО через розбіжність одиниці джерела, а відновлення
 * йде через окремий діалог `unit-changed` із рішенням «прийняти нову одиницю
 * чи лишити паузу» — це інший, складніший сценарій під `acceptSourceUnitChange`.
 * Ручного «Pause»/«Resume» в макеті немає взагалі, тобто вимоги до
 * підтвердження звідти взяти нізвідки. Рішення (людини не питали, бо це не
 * факт зі світу замовника, а судження про форму): обидві дії — реверсивний
 * перемикач стану того самого мапінгу, того самого класу, що кнопка «Collect»
 * в `SourcesPage.tsx`, і жодна з них там підтвердження не показує.
 */
export function PauseResumeAction({
  field,
}: {
  readonly field: MappedFieldPreview;
}): JSX.Element {
  const queryClient = useQueryClient();

  const toggle = useMutation({
    mutationFn: () =>
      field.isActive
        ? pauseEntityFieldMap(field.fieldMapId)
        : resumeEntityFieldMap(field.fieldMapId),
    onSuccess: async () => {
      // ⚠ Префікс ключа, а не повний: сторінка перегляду запитує його як
      // `['mapping-preview', entityId, window.fromUtc]`
      // (`pages/admin/MappingPreviewPage.tsx`), а `invalidateQueries` за
      // замовчуванням зіставляє запити ЗА ПРЕФІКСОМ, тож саме цей масив
      // застаріє запити будь-якої сутності й вікна.
      await queryClient.invalidateQueries({ queryKey: ['mapping-preview'] });
      showDone(field.isActive ? t('mapping.pauseDone') : t('mapping.resumeDone'));
    },
  });

  return (
    <Stack gap="xs" miw="fit-content">
      <Button
        size="compact-xs"
        variant="default"
        loading={toggle.isPending}
        onClick={() => toggle.mutate()}
      >
        {field.isActive ? t('mapping.pause') : t('mapping.resume')}
      </Button>

      {/* ⛔ Відмова сервера НЕ блокує перегляд (`ФВ-14.24`): банер лишається
          в межах рядка, решта таблиці — і решта сторінки — лишаються
          доступними. `onRetry` не переданий навмисно: «повторити» тут — це
          та сама кнопка вище, повторний клік. */}
      {toggle.error !== null && <ErrorAlert error={toggle.error} />}
    </Stack>
  );
}
