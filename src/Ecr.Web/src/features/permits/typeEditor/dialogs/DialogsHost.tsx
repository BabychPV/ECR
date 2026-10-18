import { useMemo, useRef, type JSX } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { showApiError } from '@/shared/ui/notify';
import type { PermitType } from '../../api/types';
import { PermitsPaths } from '../../shell/permitPaths';
import { removeTable, type TypeDraft } from '../draftModel';
import { EditorParams, readDialog, withoutDialog } from '../editorActions';
import { useTypeEditorApi } from '../editorContext';
import { selectedTableCode, typeWithDraft } from '../selection';
import { typeEditorKeys } from '../typeEditorKeys';
import { DeleteTableDialog } from './DeleteTableDialog';
import { TypeEditorDialogs } from './TypeEditorDialogs';

/**
 * Діалоги конструктора типу, підключені до стану сторінки (`useTypeEditorApi`).
 *
 * - Діалоги бачать тип СЕРВЕРА з накладеною робочою копією (`typeWithDraft`), тож лічильники,
 *   перелік таблиць і перевірка «таблиць немає» відповідають екрану, а не останньому збереженню.
 * - Локальна правка → `api.apply(draftModel…)`; чиста функція, що кинула `DraftModelError`, не
 *   валить сторінку: помилка показується тостом, копія лишається як була.
 * - Публікація й нова версія: `settle()` = `await api.flush()` і найсвіжіший тип із кешу (його
 *   `rowVersion` іде в запит); не збереглось — запиту немає. Відповідь сервера → `api.acceptServer`.
 * - Видалення типу чернетку не зберігає (вона все одно зникає) і веде на перелік типів.
 */
export function DialogsHost(): JSX.Element | null {
  const api = useTypeEditorApi();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const latestParams = useRef(params);
  latestParams.current = params;

  const type = useMemo(() => typeWithDraft(api.type, api.draft), [api.type, api.draft]);
  const open = readDialog(params);

  const settle = async (): Promise<PermitType | null> => {
    if (!(await api.flush())) return null;
    return queryClient.getQueryData<PermitType>(typeEditorKeys.detail(api.code)) ?? api.type;
  };

  const onChange = (change: (draft: TypeDraft) => TypeDraft): void => {
    api.apply((current) => {
      try {
        return change(current);
      } catch (error) {
        showApiError(error);
        return current;
      }
    });
  };

  return (
    <>
      <TypeEditorDialogs
        type={type}
        selectedTableCode={selectedTableCode(params, api.draft)}
        onChange={onChange}
        settle={settle}
        onTypeReplaced={api.acceptServer}
        onReload={() => void api.reload()}
        onDeleted={() => {
          // Чернетка типу зникає разом із ним: без цього запобіжник незбережених правок зупинив би перехід.
          api.acceptServer(api.type);
          void navigate(PermitsPaths.types);
        }}
        onSelectTable={(code) => {
          // Свіжі параметри: до цього такту діалог уже закрив себе в адресі (див. TypeEditorDialogs).
          const next = new URLSearchParams(latestParams.current);
          next.set(EditorParams.table, code);
          next.delete(EditorParams.panel);
          setParams(next, { replace: true });
        }}
      />
      {open?.id === 'delete-table' && open.tableCode !== null && (
        <DeleteTableDialog
          type={api.type}
          draft={api.draft}
          tableCode={open.tableCode}
          onRemove={(code) => {
            onChange((current) => removeTable(current, code));
          }}
          onClose={() => {
            setParams(withoutDialog(latestParams.current), { replace: true });
          }}
        />
      )}
    </>
  );
}
