-- =============================================================================
--  Check_LogInstanc.sql
--  Shows LogInstanc values for a Sales Order across ORDR, RDR1, and ADOC/ADO1.
--  Change @DocNum to the order you want to inspect.
-- =============================================================================

DECLARE @DocNum   INT = 5054686;
DECLARE @DocEntry INT;

SELECT @DocEntry = DocEntry FROM ORDR WHERE DocNum = @DocNum;

-- ---------------------------------------------------------------------------
-- 1. Sales Order header (ORDR)
-- ---------------------------------------------------------------------------
PRINT '--- ORDR (header) ---';
SELECT
    DocEntry,
    DocNum,
    DocStatus,
    LogInstanc
FROM ORDR
WHERE DocEntry = @DocEntry;

-- ---------------------------------------------------------------------------
-- 2. Sales Order lines (RDR1)
-- ---------------------------------------------------------------------------
PRINT '--- RDR1 (lines) ---';
SELECT
    DocEntry,
    LineNum,
    ItemCode,
    LogInstanc
FROM RDR1
WHERE DocEntry = @DocEntry
ORDER BY LineNum;

-- ---------------------------------------------------------------------------
-- 3. ADOC audit log — all snapshots for this document
-- ---------------------------------------------------------------------------
PRINT '--- ADOC (audit log snapshots) ---';
SELECT
    ObjType,
    DocEntry,
    LogInstanc,
    CreateDate
FROM ADOC
WHERE ObjType = '17'
  AND DocEntry = @DocEntry
ORDER BY LogInstanc DESC;

-- ---------------------------------------------------------------------------
-- 4. ADO1 lines — matching the LATEST LogInstanc in ADOC
-- ---------------------------------------------------------------------------
PRINT '--- ADO1 (latest ADOC snapshot lines) ---';
SELECT
    a.ObjType,
    a.DocEntry,
    a.LogInstanc,
    a.LineNum,
    a.ItemCode
FROM ADO1 a
WHERE a.ObjType = '17'
  AND a.DocEntry = @DocEntry
  AND a.LogInstanc = (
      SELECT MAX(LogInstanc)
      FROM ADOC
      WHERE ObjType = '17'
        AND DocEntry = @DocEntry
  )
ORDER BY a.LineNum;

-- ---------------------------------------------------------------------------
-- 5. Mismatch summary — highlight any line where LogInstanc differs
-- ---------------------------------------------------------------------------
PRINT '--- Mismatch check: RDR1 vs latest ADOC LogInstanc ---';
SELECT
    r.LineNum,
    r.ItemCode,
    r.LogInstanc                                        AS RDR1_LogInstanc,
    (SELECT MAX(LogInstanc) FROM ADOC
     WHERE ObjType = '17' AND DocEntry = @DocEntry)    AS ADOC_MaxLogInstanc,
    CASE
        WHEN r.LogInstanc = (SELECT MAX(LogInstanc) FROM ADOC
                             WHERE ObjType = '17' AND DocEntry = @DocEntry)
        THEN 'OK'
        ELSE '*** MISMATCH ***'
    END AS Status
FROM RDR1 r
WHERE r.DocEntry = @DocEntry
ORDER BY r.LineNum;
