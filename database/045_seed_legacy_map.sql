-- Suggested mappings from legacy Laboratory tests (dbo.AzDefine.ID) to the
-- LabFlow catalog, with the factor that turns the legacy (conventional)
-- unit into the SI unit LabFlow reports in. Status is 'Suggested': the ETL
-- uses them, and a reviewer moves each to 'Reviewed' (or 'Excluded').
--
-- Every factor was checked against the legacy values and printed normal
-- ranges (2026-10-03):
--   WBC       /cu mm, values 2,800-21,300, range 3500-11000  -> x 0.001 = 10*9/L
--   Platelet  /cumm,  values 150-620,      range 150-450     -> already x10^3/uL = 10*9/L
--   TSH       labelled "NMol/l" but range 0.34-5.6           -> really mIU/L
--   Hemoglobin gr/dl -> g/L x 10; glucose mg/dL -> mmol/L x 0.0555;
--   creatinine mg/dL -> umol/L x 88.42; cholesterol mg/dL -> mmol/L x 0.02586;
--   triglycerides mg/dL -> mmol/L x 0.01129; 25-OH vitamin D ng/mL -> nmol/L x 2.496.

SET NOCOUNT ON;

MERGE etl.LegacyTestMap AS m
USING (VALUES
    (1,   '82947', 'Fasting Blood Sugar',     'mg/dl',   'GLUF',  0.0555,   N'mg/dL to mmol/L'),
    (4,   '82565', 'Creatinine',              'mg/dl',   'CREA',  88.42,    N'mg/dL to umol/L'),
    (5,   '84295', 'Sodium',                  'mEq/L',   'NA',    1,        N'mEq/L = mmol/L'),
    (6,   '84132', 'Potassium',               'mEq/L',   'K',     1,        N'mEq/L = mmol/L'),
    (7,   '84478', 'Triglyceride',            'mg/dl',   'TRIG',  0.01129,  N'mg/dL to mmol/L'),
    (8,   '82465', 'Cholesterol (Total)',     'mg/dl',   'CHOL',  0.02586,  N'mg/dL to mmol/L'),
    (25,  '84450', 'A.S.T(SGOT)',             'U/L',     'AST',   1,        NULL),
    (26,  '84460', 'A.L.T(SGPT)',             'U/L',     'ALT',   1,        NULL),
    (33,  '82307', 'Vitamin D (25OH vit D3)', 'ng /mL',  'VITD',  2.496,    N'ng/mL to nmol/L'),
    (50,  '85023', 'C.B.C',                   NULL,      'CBC',   NULL,     N'Panel'),
    (60,  '85590', 'Platelet',                '/cumm',   'PLT',   1,        N'Legacy values are x10^3/uL (range 150-450)'),
    (100, '0',     'TSH',                     'NMol/l',  'TSH',   1,        N'Legacy unit label is wrong; values are mIU/L (range 0.34-5.6)'),
    (421, '80096', 'Hb A1C',                  '%',       'A1C',   1,        NULL),
    (702, '85018', 'Hemoglobin',              'gr/dl',   'HGB',   10,       N'g/dL to g/L'),
    (2062,'00000', 'WBC',                     '/cu mm',  'WBC',   0.001,    N'cells/uL to 10*9/L')
) AS s (LegacyAzId, LegacyCode, LegacyName, LegacyUnit, Code, Factor, Notes)
ON m.LegacyAzId = s.LegacyAzId
WHEN MATCHED AND m.MapStatus = 'Unmapped' THEN UPDATE SET
    TestId = (SELECT TestId FROM ref.Test WHERE Code = s.Code), Factor = s.Factor,
    MapStatus = 'Suggested', Notes = s.Notes
WHEN NOT MATCHED THEN INSERT (LegacyAzId, LegacyCode, LegacyName, LegacyUnit, TestId, Factor, MapStatus, Notes)
    VALUES (s.LegacyAzId, s.LegacyCode, s.LegacyName, s.LegacyUnit,
            (SELECT TestId FROM ref.Test WHERE Code = s.Code), s.Factor, 'Suggested', s.Notes);
