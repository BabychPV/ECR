import { useEffect, useMemo, useRef, type JSX } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { showApiError } from '@/shared/ui/notify';
import type { PermitType } from '../../api/types';
import { useMayEditPermitTypes, useMayPublishPermitTypes } from '../../permitRights';
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
  const mayEdit = useMayEditPermitTypes();
  const mayPublish = useMayPublishPermitTypes();
  // «Видалити таблицю» живе тут, а не в TypeEditorDialogs: без права його теж не відкриваємо й чистимо адресу.
  const deniedDeleteTable = open?.id === 'delete-table' && !mayEdit;
  useEffect(() => {
    if (deniedDeleteTable) setParams(withoutDialog(latestParams.current), { replace: true });
  }, [deniedDeleteTable, setParams]);

  // Вибір щойно доданої таблиці, що чекає зникнення `?dialog=` з адреси; зникає разом із хостом.
  const pendingSelect = useRef<string | null>(null);
  const applyPendingSelect = (from: URLSearchParams): void => {
    const code = pendingSelect.current;
    if (code === null) return;
    pendingSelect.current = null;
    const next = new URLSearchParams(from);
    next.set(EditorParams.table, code);
    next.delete(EditorParams.panel);
    setParams(next, { replace: true });
  };
  useEffect(() => {
    if (open === null) applyPendingSelect(params);
    // Лише за зміною адреси: `applyPendingSelect` читає refs.
  }, [params]);

  const settle = async (): Promise<PermitType | null> => {
    if (!(await api.flush())) return null;
    return queryClient.getQueryData<PermitType>(typeEditorKeys.detail(api.code)) ?? api.type;
  };

  const onChange = (change: (draft: TypeDraft) => TypeDraft): void => {
    if (!mayEdit) return;
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
        mayEdit={mayEdit}
        mayPublish={mayPublish}
        onTypeReplaced={api.acceptServer}
        onReload={() => void api.reload()}
        onDeleted={() => {
          // Чернетка типу зникає разом із ним: без цього запобіжник незбережених правок зупинив би перехід.
          api.acceptServer(api.type);
          void navigate(PermitsPaths.types);
        }}
        onSelectTable={(code) => {
          // ⚠ Адресу НЕ пишемо тут: діалог закриває себе тим самим тактом, а `latestParams` оновлюється лише
          // рендером (навігація react-router іде через `startTransition`) — запис із застарілих параметрів
          // повернув би `?dialog=` назад. Вибір чекає, доки в адресі діалогу вже немає (ефект нижче).
          pendingSelect.current = code;
          if (readDialog(latestParams.current) === null) applyPendingSelect(latestParams.current);
        }}
      />
      {open?.id === 'delete-table' && mayEdit && open.tableCode !== null && (
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
