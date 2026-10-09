DO $$ BEGIN
 IF (SELECT count(*) FROM imap_messages)<>9 THEN RAISE EXCEPTION 'Wrong removal count'; END IF;
 IF EXISTS(SELECT FROM imap_messages WHERE "Id"=101) THEN RAISE EXCEPTION 'Copy survived'; END IF;
 IF NOT EXISTS(SELECT FROM imap_messages WHERE "Id"=1 AND "ImapUid"=1
    AND "ReceivedUtc"='2019-10-13 01:27:02+00' AND NOT "IsRead") THEN RAISE EXCEPTION 'Original changed'; END IF;
 IF (SELECT count(*) FROM message_labels WHERE "MessageId"=1)<>2 THEN RAISE EXCEPTION 'Labels lost'; END IF;
 IF (SELECT count(*) FROM message_embeddings WHERE "MessageId"=1)<>2 THEN RAISE EXCEPTION 'Embeddings lost'; END IF;
 IF NOT EXISTS(SELECT FROM message_attachments WHERE "MessageId"=1
    AND lo_get(large_object_id::oid)=convert_to('abc','UTF8')) THEN RAISE EXCEPTION 'Attachment lost'; END IF;
 IF (SELECT count(*) FROM imap_messages WHERE "Id" IN (102,103,104,105))<>4 THEN RAISE EXCEPTION 'Non-exact messages removed'; END IF;
END $$;
