import { lazy, Suspense, type JSX } from 'react';
import { Skeleton } from '@mantine/core';
import { useParams, useSearchParams } from 'react-router-dom';
import { t } from '@/shared/i18n';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PermitEditorHead } from '@/features/permits/shell/PermitEditorHead';
import { PermitsPaths } from '@/features/permits/shell/permitPaths';
import { readDialog } from '@/features/permits/typeEditor/editorActions';
import { TypeEditorContext, type TypeEditorApi } from '@/features/permits/typeEditor/editorContext';
import { TypeEditorHeader } from '@/features/permits/typeEditor/TypeEditorHeader';
import { TypeEditorTabs } from '@/features/permits/typeEditor/TypeEditorTabs';
import { useTypeEditor } from '@/features/permits/typeEditor/useTypeEditor';

// Діалоги та майстри — важка частина маршруту, а на першому показі їх немає (`?dialog=` порожній):
// чанк вантажиться, лише коли адреса просить діалог (бюджет маршруту, D-132).
const DialogsHost = lazy(() =>
  import('@/features/permits/typeEditor/dialogs/DialogsHost').then((module) => ({ default: module.DialogsHost })),
);

/**
 * Конструктор типу дозволу (`/admin/permits/types/:code`): шапка, вкладки Structure | Checks |
 * Versions. Тип читається й зберігається `useTypeEditor`; діалоги — `DialogsHost` (PM-08c).
 */
export function PermitTypeEditorPage(): JSX.Element {
  const { code = '' } = useParams();
  const editor = useTypeEditor(code);
  const [params] = useSearchParams();
  const dialogRequested = readDialog(params) !== null;

  return (
    <>
      {/* Доки типу немає (завантаження, 404, 403), заголовок і «назад» дає ця шапка; далі — шапка типу. */}
      {editor.api === null && (
        <PermitEditorHead
          back={{ label: t('permits.back.types'), href: PermitsPaths.types }}
          title={code}
          mono
        />
      )}
      <AsyncBoundary<TypeEditorApi>
        isPending={editor.query.isPending || (editor.query.isSuccess && editor.api === null)}
        error={editor.query.error}
        data={editor.api ?? undefined}
        skeleton="form"
        onRetry={() => void editor.query.refetch()}
      >
        {(api) => (
          <TypeEditorContext.Provider value={api}>
            <TypeEditorHeader />
            <TypeEditorTabs />
            {dialogRequested && (
              <Suspense fallback={<Skeleton height={160} role="status" aria-label={t('common.loading')} />}>
                <DialogsHost />
              </Suspense>
            )}
          </TypeEditorContext.Provider>
        )}
      </AsyncBoundary>
    </>
  );
}
