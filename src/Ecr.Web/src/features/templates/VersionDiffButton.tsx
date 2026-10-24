import { Suspense, lazy, useState, type JSX } from 'react';
import { Button } from '@mantine/core';
import type { TemplateVersionPage } from '@/api/types';
import { t } from '@/shared/i18n';

type VersionSummary = TemplateVersionPage['items'][number];

const loadDialog = () => import('./VersionDiff');

const VersionDiffDialog = lazy(async () => ({ default: (await loadDialog()).VersionDiffDialog }));

/**
 * Кнопка «Порівняти» шапки конструктора версії — та сама, що в `VersionDiff`, але діалог за `import()`.
 *
 * ⛔ Причина — бюджет маршруту (`D-132`): діалог тягнув `Select` (Combobox, опції, `CheckIcon` — ~6 КБ gzip),
 * а відкривають його одиниці з тих, хто відкриває версію. Поведінка та сама, що у `VersionDiff`:
 *
 * - прапорець `mounted` односторонній — доки не відкривали, діалогу в дереві немає (закрита `Modal` і так
 *   нічого не малює); відкрили раз — далі він стоїть, як і доти: анімація закриття ціла, обрана друга версія
 *   переживає закриття;
 * - `fallback={null}`: чанк із того самого походження приходить за десятки мілісекунд, а заглушка-копія
 *   діалогу перемонтувала б його посеред вводу;
 * - чанк вантажиться заздалегідь, щойно курсор чи фокус на кнопці, — до натискання він уже в кеші.
 */
export function VersionDiffButton({
  templateVersionId,
  versions,
}: {
  templateVersionId: number;
  versions: readonly VersionSummary[] | undefined;
}): JSX.Element {
  const [opened, setOpened] = useState(false);
  const [mounted, setMounted] = useState(false);
  // ⚠ Невдале передзавантаження мовчить: `lazy` при відкритті спробує сам і покаже помилку межі.
  const preload = (): void => void loadDialog().catch(() => undefined);

  return (
    <>
      <Button
        variant="default"
        onPointerEnter={preload}
        onFocus={preload}
        onClick={() => {
          setMounted(true);
          setOpened(true);
        }}
      >
        {t('version.diff')}
      </Button>

      {mounted && (
        <Suspense fallback={null}>
          <VersionDiffDialog
            templateVersionId={templateVersionId}
            versions={versions}
            opened={opened}
            onClose={() => setOpened(false)}
          />
        </Suspense>
      )}
    </>
  );
}
