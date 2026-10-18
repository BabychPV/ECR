import type { JSX } from 'react';
import { useParams } from 'react-router-dom';
import { t } from '@/shared/i18n';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PermitEditorHead } from '@/features/permits/shell/PermitEditorHead';
import { PermitsPaths } from '@/features/permits/shell/permitPaths';
import { DialogsHost } from '@/features/permits/typeEditor/dialogs/DialogsHost';
import { TypeEditorContext, type TypeEditorApi } from '@/features/permits/typeEditor/editorContext';
import { TypeEditorHeader } from '@/features/permits/typeEditor/TypeEditorHeader';
import { TypeEditorTabs } from '@/features/permits/typeEditor/TypeEditorTabs';
import { useTypeEditor } from '@/features/permits/typeEditor/useTypeEditor';

/**
 * Конструктор типу дозволу (`/admin/permits/types/:code`): шапка, вкладки Structure | Checks |
 * Versions. Тип читається й зберігається `useTypeEditor`; діалоги — `DialogsHost` (PM-08c).
 */
export function PermitTypeEditorPage(): JSX.Element {
  const { code = '' } = useParams();
  const editor = useTypeEditor(code);

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
            <DialogsHost />
          </TypeEditorContext.Provider>
        )}
      </AsyncBoundary>
    </>
  );
}
