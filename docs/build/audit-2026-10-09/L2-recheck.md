# L2 recheck @3a3818ef (agent a5e26e3e)
All L2-01..13 FIXED (AN-32 series 03-04.10 + a64c0000 AN-38b). No regressions.
Tails:
- L2-01 cosmetic: JobWorker.cs:504-514 writes retryScheduled message BEFORE RequeueAsync; DbJobQueue.cs:511-518 closes as Cancelled if CancelRequestedAt -> Cancelled row with "retry scheduled" text. Fix: in UPDATE set [Message]=CASE WHEN CancelRequestedAt IS NULL THEN [Message] ELSE NULL END. Test: extend DbJobQueueCancelOnRequeueTests Requeue case, DoesNotContain retryScheduled.
- L2-11: DbJobQueue.cs:631-633 CoveredBy(covering ?? "") -> 409 with empty coveredBy, text "задача ,". Fix: SELECT State IN ('Queued','Running') Queued first, or DbBackgroundJobScheduler.cs:181 handle empty.
- L2-09 process: no Windows proof (no mutation/20x); need CI worker(windows) 20/20 + record WORK-QUEUE.md:148; extend WorkerSupervisorTests.cs:147 to assert row Queued ReclaimCount=0.
- Note L2-03: Api MaxDuration=null (only ChildComposition.cs:58) by design I1; Evaluate of one formula doesn't see token.
- Docs: WORK-QUEUE.md:68 AN-32 still todo.
