-- Reference data: departments, units, specimen types, a starter test catalog and adult ranges.
--
-- Ranges are typical adult SI values for demonstration only. A real lab must use the ranges
-- validated for its own analyzers and methods. LOINC codes should be re-checked against pCLOCD.
-- OhipFeeCode is left NULL until filled from the current Schedule of Benefits for Laboratory Services.
-- UninsuredPrice values are placeholders, not real market prices.

SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

INSERT ref.Department (Code, NameEn, NameFr, SortOrder) VALUES
    ('CHEM',  N'Clinical Chemistry', N'Biochimie clinique',   10),
    ('HEM',   N'Hematology',         N'Hématologie',          20),
    ('COAG',  N'Coagulation',        N'Coagulation',          30),
    ('ENDO',  N'Endocrinology',      N'Endocrinologie',       40),
    ('IMM',   N'Immunology',         N'Immunologie',          50),
    ('UA',    N'Urinalysis',         N'Analyse d''urine',     60),
    ('MICRO', N'Microbiology',       N'Microbiologie',        70);

INSERT ref.Unit (UnitCode, Display) VALUES
    ('mmol/L',  N'mmol/L'),
    ('umol/L',  N'µmol/L'),
    ('nmol/L',  N'nmol/L'),
    ('g/L',     N'g/L'),
    ('ug/L',    N'µg/L'),
    ('U/L',     N'U/L'),
    ('m[IU]/L', N'mIU/L'),
    ('%',       N'%'),
    ('10*9/L',  N'×10⁹/L'),
    ('10*12/L', N'×10¹²/L'),
    ('fL',      N'fL'),
    ('{ratio}', N'ratio');

INSERT ref.SpecimenType (SpecimenTypeCode, NameEn, NameFr, Container) VALUES
    ('SER',  N'Serum',       N'Sérum',        N'Gold top (SST)'),
    ('PLAS', N'Plasma',      N'Plasma',       N'Light green top (lithium heparin)'),
    ('BLD',  N'Whole blood', N'Sang total',   N'Lavender top (EDTA)'),
    ('UR',   N'Urine',       N'Urine',        N'Sterile urine container');

DECLARE @CHEM smallint = (SELECT DepartmentId FROM ref.Department WHERE Code = 'CHEM'),
        @HEM  smallint = (SELECT DepartmentId FROM ref.Department WHERE Code = 'HEM'),
        @ENDO smallint = (SELECT DepartmentId FROM ref.Department WHERE Code = 'ENDO');

INSERT ref.Test (Code, LoincCode, NameEn, NameFr, ShortName, DepartmentId, SpecimenTypeCode, ResultType, UnitCode,
                 DecimalPlaces, ApplicableSex, IsOhipInsured, UninsuredPrice, FastingRequired, PreparationEn, TurnaroundHours)
VALUES
    -- chemistry
    ('GLUF', '14771-0', N'Glucose, fasting',          N'Glucose à jeun',               N'Glucose F', @CHEM, 'SER', 'Numeric', 'mmol/L',  1, NULL, 1, NULL, 1, N'Fast for 8-12 hours. Water is allowed.', 24),
    ('CREA', '14682-9', N'Creatinine',                N'Créatinine',                   N'Creat',     @CHEM, 'SER', 'Numeric', 'umol/L',  0, NULL, 1, NULL, 0, NULL, 24),
    ('NA',   '2951-2',  N'Sodium',                    N'Sodium',                       N'Na',        @CHEM, 'SER', 'Numeric', 'mmol/L',  0, NULL, 1, NULL, 0, NULL, 24),
    ('K',    '2823-3',  N'Potassium',                 N'Potassium',                    N'K',         @CHEM, 'SER', 'Numeric', 'mmol/L',  1, NULL, 1, NULL, 0, NULL, 24),
    ('CHOL', '14647-2', N'Cholesterol, total',        N'Cholestérol total',            N'Chol',      @CHEM, 'SER', 'Numeric', 'mmol/L',  2, NULL, 1, NULL, 0, NULL, 24),
    ('TRIG', '14927-8', N'Triglycerides',             N'Triglycérides',                N'TG',        @CHEM, 'SER', 'Numeric', 'mmol/L',  2, NULL, 1, NULL, 0, NULL, 24),
    ('HDL',  '14646-4', N'HDL cholesterol',           N'Cholestérol HDL',              N'HDL',       @CHEM, 'SER', 'Numeric', 'mmol/L',  2, NULL, 1, NULL, 0, NULL, 24),
    ('LDLC', '39469-2', N'LDL cholesterol, calculated', N'Cholestérol LDL (calculé)',  N'LDL',       @CHEM, 'SER', 'Numeric', 'mmol/L',  2, NULL, 1, NULL, 0, NULL, 24),
    ('ALT',  '1742-6',  N'Alanine aminotransferase',  N'Alanine aminotransférase',     N'ALT',       @CHEM, 'SER', 'Numeric', 'U/L',     0, NULL, 1, NULL, 0, NULL, 24),
    ('AST',  '1920-8',  N'Aspartate aminotransferase', N'Aspartate aminotransférase',  N'AST',       @CHEM, 'SER', 'Numeric', 'U/L',     0, NULL, 1, NULL, 0, NULL, 24),
    ('FERR', '2276-4',  N'Ferritin',                  N'Ferritine',                    N'Ferritin',  @CHEM, 'SER', 'Numeric', 'ug/L',    0, NULL, 1, NULL, 0, NULL, 48),
    ('VITD', '14635-7', N'25-Hydroxyvitamin D',       N'25-hydroxyvitamine D',         N'Vit D',     @CHEM, 'SER', 'Numeric', 'nmol/L',  0, NULL, 0, 51.00, 0,
        N'Insured by OHIP only for specific conditions (e.g. osteoporosis, malabsorption, renal disease); otherwise patient-paid.', 72),
    ('PSA',  '2857-1',  N'Prostate specific antigen', N'Antigène prostatique spécifique', N'PSA',    @CHEM, 'SER', 'Numeric', 'ug/L',    2, 'M',  0, 40.00, 0,
        N'Screening PSA is patient-paid; monitoring of diagnosed prostate cancer is insured.', 48),
    -- endocrinology
    ('A1C',  '4548-4',  N'Hemoglobin A1c',            N'Hémoglobine A1c',              N'HbA1c',     @ENDO, 'BLD', 'Numeric', '%',       1, NULL, 1, NULL, 0, NULL, 48),
    ('TSH',  '3016-3',  N'Thyroid stimulating hormone', N'Thyréostimuline',            N'TSH',       @ENDO, 'SER', 'Numeric', 'm[IU]/L', 2, NULL, 1, NULL, 0, NULL, 48),
    -- hematology
    ('HGB',  '718-7',   N'Hemoglobin',                N'Hémoglobine',                  N'Hb',        @HEM,  'BLD', 'Numeric', 'g/L',     0, NULL, 1, NULL, 0, NULL, 24),
    ('WBC',  '6690-2',  N'White blood cell count',    N'Leucocytes',                   N'WBC',       @HEM,  'BLD', 'Numeric', '10*9/L',  1, NULL, 1, NULL, 0, NULL, 24),
    ('PLT',  '777-3',   N'Platelet count',            N'Plaquettes',                   N'PLT',       @HEM,  'BLD', 'Numeric', '10*9/L',  0, NULL, 1, NULL, 0, NULL, 24),
    -- panels
    ('LIPID', NULL,     N'Lipid profile',             N'Bilan lipidique',              N'Lipids',    @CHEM, 'SER', 'Panel',   NULL,   NULL, NULL, 1, NULL, 0,
        N'Fasting is not required unless triglycerides were previously above 4.5 mmol/L.', 24),
    ('LYTES', NULL,     N'Electrolytes',              N'Électrolytes',                 N'Lytes',     @CHEM, 'SER', 'Panel',   NULL,   NULL, NULL, 1, NULL, 0, NULL, 24),
    ('CBC',   NULL,     N'Complete blood count',      N'Formule sanguine complète',    N'CBC',       @HEM,  'BLD', 'Panel',   NULL,   NULL, NULL, 1, NULL, 0, NULL, 24);

INSERT ref.PanelMember (PanelTestId, MemberTestId, SortOrder)
SELECT p.TestId, m.TestId, v.SortOrder
FROM (VALUES ('LIPID','CHOL',1), ('LIPID','TRIG',2), ('LIPID','HDL',3), ('LIPID','LDLC',4),
             ('LYTES','NA',1),   ('LYTES','K',2),
             ('CBC','WBC',1),    ('CBC','HGB',2),    ('CBC','PLT',3)) v(Panel, Member, SortOrder)
JOIN ref.Test p ON p.Code = v.Panel
JOIN ref.Test m ON m.Code = v.Member;

-- Adult ranges (18+ years = 6570 days)
INSERT ref.ReferenceRange (TestId, Sex, AgeFromDays, Low, High, CriticalLow, CriticalHigh, DisplayText, Comment, EffectiveFrom)
SELECT t.TestId, v.Sex, 6570, v.Low, v.High, v.CritLow, v.CritHigh, v.DisplayText, v.Comment, '2020-01-01'
FROM (VALUES
    ('GLUF','U', 3.6,   6.0,   2.5,  25.0,  N'3.6 - 6.0',   N'6.1-6.9 impaired fasting glucose; 7.0 or higher consistent with diabetes (Diabetes Canada).'),
    ('CREA','M', 60,    110,   NULL, NULL,  N'60 - 110',    NULL),
    ('CREA','F', 45,    90,    NULL, NULL,  N'45 - 90',     NULL),
    ('NA',  'U', 135,   145,   120,  160,   N'135 - 145',   NULL),
    ('K',   'U', 3.5,   5.0,   2.8,  6.2,   N'3.5 - 5.0',   NULL),
    ('CHOL','U', NULL,  5.20,  NULL, NULL,  N'< 5.20',      NULL),
    ('TRIG','U', NULL,  1.70,  NULL, NULL,  N'< 1.70',      NULL),
    ('HDL', 'M', 1.00,  NULL,  NULL, NULL,  N'> 1.00',      NULL),
    ('HDL', 'F', 1.30,  NULL,  NULL, NULL,  N'> 1.30',      NULL),
    ('LDLC','U', NULL,  3.50,  NULL, NULL,  N'< 3.50',      N'Treatment targets depend on cardiovascular risk (CCS dyslipidemia guidelines).'),
    ('ALT', 'U', NULL,  40,    NULL, NULL,  N'<= 40',       NULL),
    ('AST', 'U', NULL,  40,    NULL, NULL,  N'<= 40',       NULL),
    ('FERR','M', 30,    400,   NULL, NULL,  N'30 - 400',    NULL),
    ('FERR','F', 15,    200,   NULL, NULL,  N'15 - 200',    NULL),
    ('VITD','U', 75,    250,   NULL, NULL,  N'75 - 250',    N'Below 25 deficient; 25-74 insufficient; 75-250 sufficient.'),
    ('PSA', 'M', NULL,  4.00,  NULL, NULL,  N'< 4.00',      NULL),
    ('A1C', 'U', 4.0,   5.9,   NULL, NULL,  N'4.0 - 5.9',   N'6.0-6.4 prediabetes; 6.5 or higher consistent with diabetes (Diabetes Canada).'),
    ('TSH', 'U', 0.40,  4.00,  NULL, NULL,  N'0.40 - 4.00', NULL),
    ('HGB', 'M', 135,   175,   70,   200,   N'135 - 175',   NULL),
    ('HGB', 'F', 120,   160,   70,   200,   N'120 - 160',   NULL),
    ('WBC', 'U', 4.0,   11.0,  1.0,  30.0,  N'4.0 - 11.0',  NULL),
    ('PLT', 'U', 150,   400,   20,   1000,  N'150 - 400',   NULL)
) v(Code, Sex, Low, High, CritLow, CritHigh, DisplayText, Comment)
JOIN ref.Test t ON t.Code = v.Code;

COMMIT;
