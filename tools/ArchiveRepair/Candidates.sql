-- Only the preserved-original/re-import pattern; never deduplicate distinct folders
-- or use Message-ID alone. The ID boundary is captured during read-only diagnosis.
CREATE TEMP TABLE repair_candidates ON COMMIT DROP AS
SELECT o."Id" AS keep_id, n."Id" AS remove_id
FROM public.imap_messages n
JOIN public.imap_messages o ON n."FolderId" = o."FolderId" AND n."MessageId" = o."MessageId"
JOIN public.message_bodies nb ON nb."MessageId" = n."Id"
JOIN public.message_bodies ob ON ob."MessageId" = o."Id"
WHERE n."Id" > @boundary AND o."Id" <= @boundary
  AND n."ImapUid" > 0 AND o."ImapUid" < 0
  AND n."MessageId" <> '' AND n."MessageId" NOT LIKE 'uid:%'
  AND n."Subject" = o."Subject" AND n."FromName" = o."FromName" AND n."FromAddress" = o."FromAddress"
  AND nb."Headers" IS NOT NULL AND nb."Headers" = ob."Headers"
  AND nb."PlainText" IS NOT DISTINCT FROM ob."PlainText"
  AND nb."HtmlText" IS NOT DISTINCT FROM ob."HtmlText"
  AND EXISTS (SELECT FROM public.message_embeddings e WHERE e."MessageId" = n."Id" AND e."CreatedAt" >= @since)
  AND NOT EXISTS (SELECT FROM public.message_embeddings e WHERE e."MessageId" = n."Id" AND e."CreatedAt" < @since)
  AND NOT EXISTS (
    (SELECT "FileName", "ContentType", content_id, "Disposition", "SizeBytes", "Hash" FROM public.message_attachments WHERE "MessageId" = o."Id"
     EXCEPT ALL
     SELECT "FileName", "ContentType", content_id, "Disposition", "SizeBytes", "Hash" FROM public.message_attachments WHERE "MessageId" = n."Id")
    UNION ALL
    (SELECT "FileName", "ContentType", content_id, "Disposition", "SizeBytes", "Hash" FROM public.message_attachments WHERE "MessageId" = n."Id"
     EXCEPT ALL
     SELECT "FileName", "ContentType", content_id, "Disposition", "SizeBytes", "Hash" FROM public.message_attachments WHERE "MessageId" = o."Id"))
  AND NOT EXISTS (SELECT FROM public.message_attachments a WHERE a."MessageId" IN (o."Id", n."Id") AND a.large_object_id = 0);

CREATE TEMP TABLE repair_attachment_pairs ON COMMIT DROP AS
WITH attachments AS (
 SELECT a.*, row_number() OVER (PARTITION BY "MessageId" ORDER BY "FileName", "ContentType", content_id,
     "Disposition", "SizeBytes", "Hash", "Id") AS ordinal
 FROM public.message_attachments a
 WHERE a."MessageId" IN (SELECT keep_id FROM repair_candidates UNION SELECT remove_id FROM repair_candidates)
)
SELECT p.keep_id, p.remove_id, a.large_object_id::oid AS left_oid, b.large_object_id::oid AS right_oid
FROM repair_candidates p JOIN attachments a ON a."MessageId"=p.keep_id
JOIN attachments b ON b."MessageId"=p.remove_id AND b.ordinal=a.ordinal;

CREATE TEMP TABLE repair_plan (keep_id integer PRIMARY KEY, remove_id integer UNIQUE NOT NULL) ON COMMIT DROP;
