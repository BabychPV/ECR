import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import { registerUnsavedSource } from '@/shared/ui/unsavedSources';
import { getPermitType, replacePermitType } from '../api/typesApi';
import type { PermitType } from '../api/types';
import { countChanges, draftOf, sameDraft, validateDraft, type DraftProblem, type TypeDraft } from './draftModel';
import type { SaveState, TypeEditorApi } from './editorContext';
import { typeEditorKeys } from './typeEditorKeys';

/*
 * Робоча копія типу + збереження (Permits, PM-08b).
 *
 * Стратегія: явне відкладене збереження — через `AutosaveDelayMs` після останньої правки. Копія, яка
 * не проходить клієнтські перевірки (`validateDraft`, ті самі `messageKey`, що й `422` сервера), НЕ
 * відправляється: інакше кожна проміжна правка (нова таблиця ще без ключа, список без значень) давала
 * б червону відмову. Такі правки лишаються в копії, шапка каже «N unsaved changes», а смуга над
 * структурою — що саме виправити.
 *
 * Паралельне редагування: `PUT` несе `rowVersion`; `409 ECR-PRM-4093` — інша людина змінила чи
 * опублікувала тип. Копію не затираємо мовчки: стан `conflict`, і лише явне «Reload» відкидає її.
 */

export const AutosaveDelayMs = 1500;
const UnsavedSourceId = 'permit-type-editor';
const ConflictCode = 'ECR-PRM-4093';

interface Core {
  draft: TypeDraft;
  base: TypeDraft;
  rowVersion: string;
}

export interface TypeEditorHook {
  readonly query: UseQueryResult<PermitType>;
  /** `null`, доки тип не завантажено. */
  readonly api: TypeEditorApi | null;
  /** Чого бракує копії, щоб її прийняв сервер. */
  readonly problems: readonly DraftProblem[];
  /** Остання помилка збереження, що не є конфліктом; `null` — немає. */
  readonly saveError: unknown;
  /** Повторити збереження після помилки. */
  retry(): void;
}

function bodyOf(draft: TypeDraft, rowVersion: string) {
  return {
    category: draft.category,
    nameL10n: { ...draft.nameL10n },
    rules: [...draft.rules],
    tables: [...draft.tables],
    rowVersion,
  };
}

export function useTypeEditor(code: string): TypeEditorHook {
  const queryClient = useQueryClient();
  const key = typeEditorKeys.detail(code);
  const query = useQuery({
    queryKey: key,
    queryFn: () => getPermitType(code),
    // Перечитування у фоні затерло б правки, що ще не доїхали на сервер.
    staleTime: Infinity,
    refetchOnWindowFocus: false,
    retry: false,
  });

  const core = useRef<Core | null>(null);
  const inflight = useRef<Promise<boolean> | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const conflicted = useRef(false);
  const failed = useRef(false);
  const [draft, setDraft] = useState<TypeDraft | null>(null);
  const [base, setBase] = useState<TypeDraft | null>(null);
  const [save, setSave] = useState<SaveState>({ kind: 'idle' });
  const [saveError, setSaveError] = useState<unknown>(null);

  const adopt = useCallback((type: PermitType): void => {
    const next = draftOf(type);
    core.current = { draft: next, base: next, rowVersion: type.rowVersion };
    conflicted.current = false;
    failed.current = false;
    setDraft(next);
    setBase(next);
    setSaveError(null);
  }, []);

  const serverType = query.data;
  useEffect(() => {
    if (serverType === undefined) return;
    const dirty = core.current !== null && !sameDraft(core.current.draft, core.current.base);
    if (core.current === null || !dirty) {
      adopt(serverType);
      setSave((current) => (current.kind === 'saved' ? current : { kind: 'idle' }));
    }
  }, [serverType, adopt]);

  const editable = serverType?.hasUnpublishedChanges ?? false;

  const problemsOf = useCallback((value: TypeDraft): DraftProblem[] => validateDraft(value, { typeCode: code }), [code]);

  const clearTimer = useCallback((): void => {
    if (timer.current !== null) {
      clearTimeout(timer.current);
      timer.current = null;
    }
  }, []);

  const runSave = useCallback((): Promise<boolean> => {
    const current = core.current;
    if (current === null) return Promise.resolve(true);
    if (inflight.current !== null) {
      return inflight.current.then(() => runSave());
    }
    if (sameDraft(current.draft, current.base)) return Promise.resolve(true);
    if (conflicted.current || problemsOf(current.draft).length > 0) return Promise.resolve(false);

    const sent = current.draft;
    setSave({ kind: 'saving' });
    const request = replacePermitType(code, bodyOf(sent, current.rowVersion))
      .then((result) => {
        queryClient.setQueryData(key, result);
        const saved = draftOf(result);
        const live = core.current;
        if (live !== null) {
          live.base = saved;
          live.rowVersion = result.rowVersion;
          // Правки, зроблені під час запиту, не губимо: копією лишається нова, основою — збережена.
          if (live.draft === sent) live.draft = saved;
          setDraft(live.draft);
          setBase(saved);
        }
        failed.current = false;
        setSaveError(null);
        setSave({ kind: 'saved', at: new Date() });
        return true;
      })
      .catch((error: unknown) => {
        if (error instanceof EcrApiError && error.problem.errorCode === ConflictCode) {
          conflicted.current = true;
          setSave({ kind: 'conflict' });
        } else {
          failed.current = true;
          setSave({ kind: 'failed', count: countChanges(current.base, sent) });
        }
        setSaveError(error);
        return false;
      })
      .finally(() => {
        inflight.current = null;
      });
    inflight.current = request;
    return request;
  }, [code, key, problemsOf, queryClient]);

  const schedule = useCallback((): void => {
    clearTimer();
    timer.current = setTimeout(() => {
      timer.current = null;
      void runSave().then((ok) => {
        const live = core.current;
        // Правки, що з'явилися під час запиту, чекають свого збереження.
        if (ok && live !== null && !sameDraft(live.draft, live.base)) schedule();
      });
    }, AutosaveDelayMs);
  }, [clearTimer, runSave]);

  useEffect(() => clearTimer, [clearTimer]);

  const apply = useCallback(
    (change: (value: TypeDraft) => TypeDraft): void => {
      const live = core.current;
      if (live === null || !editable) return;
      const next = change(live.draft);
      if (next === live.draft) return;
      live.draft = next;
      setDraft(next);
      failed.current = false;
      if (sameDraft(next, live.base)) {
        // Вернули як було: зберігати нічого.
        clearTimer();
        setSave({ kind: 'idle' });
        return;
      }
      setSave({ kind: 'dirty', count: countChanges(live.base, next) });
      if (!conflicted.current) schedule();
    },
    [clearTimer, editable, schedule],
  );

  const flush = useCallback(async (): Promise<boolean> => {
    clearTimer();
    for (let attempt = 0; attempt < 5; attempt += 1) {
      const ok = await runSave();
      const live = core.current;
      if (!ok) return live === null || sameDraft(live.draft, live.base);
      if (live === null || sameDraft(live.draft, live.base)) return true;
    }
    return false;
  }, [clearTimer, runSave]);

  const acceptServer = useCallback(
    (type: PermitType): void => {
      clearTimer();
      queryClient.setQueryData(key, type);
      adopt(type);
      setSave({ kind: 'idle' });
    },
    [adopt, clearTimer, key, queryClient],
  );

  const reload = useCallback(async (): Promise<void> => {
    clearTimer();
    const fresh = await queryClient.fetchQuery({ queryKey: key, queryFn: () => getPermitType(code), staleTime: 0 });
    adopt(fresh);
    setSave({ kind: 'idle' });
  }, [adopt, clearTimer, code, key, queryClient]);

  const retry = useCallback((): void => {
    failed.current = false;
    void runSave();
  }, [runSave]);

  const dirtyCount = draft !== null && base !== null ? countChanges(base, draft) : 0;

  useEffect(
    () =>
      registerUnsavedSource(UnsavedSourceId, {
        hasUnsaved: () => core.current !== null && !sameDraft(core.current.draft, core.current.base),
        unsavedCount: () => (core.current === null ? 0 : Math.max(1, countChanges(core.current.base, core.current.draft))),
        flush: () => flush(),
      }),
    [flush],
  );

  const problems = useMemo(() => (draft === null ? [] : problemsOf(draft)), [draft, problemsOf]);

  const shownSave = useMemo<SaveState>(
    () => (save.kind === 'idle' && dirtyCount > 0 ? { kind: 'dirty', count: dirtyCount } : save),
    [save, dirtyCount],
  );

  const api = useMemo<TypeEditorApi | null>(() => {
    if (serverType === undefined || draft === null) return null;
    return { code, type: serverType, draft, editable, save: shownSave, apply, flush, acceptServer, reload };
  }, [serverType, draft, editable, shownSave, apply, flush, acceptServer, reload, code]);

  return { query, api, problems, saveError, retry };
}
