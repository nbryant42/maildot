-- Run only in a disposable maildot_test_* database.
CREATE TABLE imap_folders ("Id" integer PRIMARY KEY);
INSERT INTO imap_folders VALUES (1),(2);
CREATE TABLE imap_messages (
 "Id" integer PRIMARY KEY, "FolderId" integer REFERENCES imap_folders,
 "ImapUid" bigint, "MessageId" text, "Subject" text, "FromName" text, "FromAddress" text,
 "ReceivedUtc" timestamptz, "IsRead" boolean, "Hash" text, UNIQUE("FolderId","ImapUid"));
CREATE TABLE message_bodies ("MessageId" integer PRIMARY KEY REFERENCES imap_messages ON DELETE CASCADE,
 "Headers" jsonb, "PlainText" text, "HtmlText" text);
CREATE TABLE message_attachments ("Id" integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 "MessageId" integer REFERENCES imap_messages ON DELETE CASCADE, "FileName" text, "ContentType" text,
 content_id text, "Disposition" text, "SizeBytes" bigint, "Hash" text, large_object_id bigint);
CREATE TABLE message_embeddings ("MessageId" integer REFERENCES imap_messages ON DELETE CASCADE,
 "ChunkIndex" integer, "Vector" text, "ModelVersion" text, "CreatedAt" timestamptz, PRIMARY KEY("MessageId","ChunkIndex"));
CREATE TABLE message_labels ("LabelId" integer, "MessageId" integer REFERENCES imap_messages ON DELETE CASCADE,
 PRIMARY KEY("LabelId","MessageId"));
-- 1: exact duplicate; 2: different bytes with identical metadata;
-- 3: different body; 4: copy before cutoff; 5: same message in different folders.
INSERT INTO imap_messages
SELECT id, 1, -id, 'message-'||id, 'subject','sender','sender@example.test','2019-10-13 01:27:02+00',false,'old'
FROM generate_series(1,5) id;
INSERT INTO imap_messages
SELECT id+100, CASE WHEN id=5 THEN 2 ELSE 1 END, id, 'message-'||id, 'subject','sender','sender@example.test',
 '2026-10-09 14:38:19+00',true,'new' FROM generate_series(1,5) id;
INSERT INTO message_bodies
SELECT "Id", '{"Date":"Sat, 12 Oct 2019 21:27:02 -0400"}',CASE WHEN "Id"=103 THEN 'changed' ELSE 'body' END,NULL
FROM imap_messages;
INSERT INTO message_embeddings SELECT "Id",0,'vector','test',
CASE WHEN "Id"=104 THEN '2026-09-25'::timestamptz ELSE '2026-10-09'::timestamptz END FROM imap_messages;
INSERT INTO message_embeddings VALUES (101,1,'extra vector','test','2026-10-09');
INSERT INTO message_labels VALUES (1,1),(2,101),(1,101);
INSERT INTO message_attachments ("MessageId","FileName","ContentType",content_id,"Disposition","SizeBytes","Hash",large_object_id)
SELECT id,'file','application/octet-stream',NULL,'attachment',3,'same hash',
lo_from_bytea(0,convert_to(CASE WHEN id=102 THEN 'bad' ELSE 'abc' END,'UTF8')) FROM unnest(ARRAY[1,101,2,102]) id;
