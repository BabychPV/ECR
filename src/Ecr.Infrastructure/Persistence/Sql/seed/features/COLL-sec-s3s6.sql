-- COLL:sec-s3s6 ── ru/kz межі явних адрес каналу (рев'ю ent6 S3); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0422.channelTooManyRecipients', N'ru', N'Канал принимает не более 50 явных адресов получателей.'),
    (N'err.ECR-REQ-0422.channelTooManyRecipients', N'kz', N'Арна ең көбі 50 айқын алушы мекенжайын қабылдайды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:sec-s3s6 ── кінець секції ──

