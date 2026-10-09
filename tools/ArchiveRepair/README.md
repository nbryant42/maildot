# One-time archive repair

This tool repairs the negative-original / positive-reimport pattern caused by establishing
IMAP UIDVALIDITY on an existing archive. It is deliberately separate from app startup.

Build and run on Windows. It reads the app's PostgreSQL settings and Windows vault credential.
Default execution is a dry run; all temporary work is rolled back.

```powershell
dotnet run --project tools/ArchiveRepair -c Release -- --archive-boundary 44107 --since 2026-09-26T04:00:00Z
```

The boundary is the maximum archived-original ID observed during diagnosis, **not a universal
constant**. The cutoff above is September 26 midnight in America/New_York. The database has
no message creation timestamp: the boundary and copy embedding timestamps constrain this
specific import cohort. Review these values against the database before any future use.

Candidates must share a folder, non-synthetic Message-ID, subject, sender, complete stored
headers, plain text, HTML, and attachment metadata. Attachment bytes are compared through
EOF. Copies with pre-cutoff embeddings or without embedding provenance are excluded.
Distinct live UIDs are only merged one-for-one with archived originals. Differing messages
are retained. Generated sanitized HTML is not used as message identity.

To apply a reviewed plan, add `--apply --expected-count <dry-run-count>`. A mismatch aborts.
The transaction locks relevant tables against concurrent writes, snapshots both sides in
a `maildot_repair_<UTC timestamp>` schema, unions labels and missing embedding chunks,
removes the newer copies, and assigns their server UIDs to the original records. Original
IDs, dates, bodies, and attachments survive. Unread wins if read flags disagree.

Recovery tables intentionally remain in PostgreSQL; attachment large objects referenced by
the snapshots are not unlinked. Recovery is a manual database operation using the snapshot
and `plan` table, accounting for subsequent sync activity. Do not remove recovery tables or
large objects without separately reviewing retention needs.

For regression tests, use a disposable database named `maildot_test_*`, load `TestFixture.sql`,
set `MAILDOT_REPAIR_TEST_CONNECTION`, and run with boundary `5`, the cutoff above, and
`--apply --expected-count 1`. Run `TestAssertions.sql` afterward. A second dry run must select
zero duplicates. Fixtures cover changed attachment bytes despite identical metadata,
changed bodies, pre-cutoff copies, cross-folder messages, labels, embeddings, and original dates.
