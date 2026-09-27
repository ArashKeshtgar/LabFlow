-- Sequences for human-readable numbers. The app formats them:
--   AccessionNumber  LF<yy>-<6 digits>   e.g. LF26-000042
--   Mrn              LF<7 digits>        e.g. LF0000042
-- They start above the demo data so seeds never collide with app-issued numbers.

CREATE SEQUENCE lab.AccessionSeq AS int START WITH 1000 INCREMENT BY 1 NO CYCLE;
GO
CREATE SEQUENCE core.MrnSeq AS int START WITH 1000 INCREMENT BY 1 NO CYCLE;
GO
