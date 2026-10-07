"""Generates the LabFlow Power BI project (PBIP) from code.

    python bi/build_report.py        ->  bi/LabFlow.pbip + LabFlow.SemanticModel/ + LabFlow.Report/

The semantic model is written as TMDL and the report as PBIR JSON, so both
are plain text that diffs in Git. Open bi/LabFlow.pbip in Power BI Desktop and
refresh; it reads the de-identified warehouse in Azure SQL (LabFlowDW), which
the Azure Data Factory pipeline in etl/adf keeps in step with the on-prem dw.
Rerunning this script overwrites the generated folders.
"""
import hashlib
import json
import shutil
from pathlib import Path

HERE = Path(__file__).resolve().parent
SERVER = "labflow-arash-sql.database.windows.net"
DATABASE = "LabFlowDW"

SM = HERE / "LabFlow.SemanticModel"
RP = HERE / "LabFlow.Report"
SCHEMA = "https://developer.microsoft.com/json-schemas/fabric"


def write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text.replace("\r\n", "\n"), encoding="utf-8", newline="\n")


def write_json(path: Path, obj) -> None:
    write(path, json.dumps(obj, indent=2, ensure_ascii=False) + "\n")


def q(name: str) -> str:
    """TMDL / DAX object name, quoted when it is not a plain identifier."""
    return name if name.replace("_", "").isalnum() else "'" + name.replace("'", "''") + "'"


# ----------------------------------------------------------------- semantic model

# (table, source schema, source table, [(column, source column, dataType, isHidden, formatString)])
TABLES = [
    ("Results", "dw", "FactLabResult", [
        ("ResultKey", "ResultKey", "int64", True, None),
        ("VisitNo", "LegacyVisitNo", "int64", True, None),
        ("TestKey", "TestKey", "int64", True, None),
        ("PatientKey", "PatientKey", "int64", True, None),
        ("PayerKey", "PayerKey", "int64", True, None),
        ("VisitDateKey", "VisitDateKey", "int64", True, None),
        ("ResultDateKey", "ResultDateKey", "int64", True, None),
        ("AgeBand", "AgeBand", "string", False, None),
        ("HasAnswer", "HasAnswer", "boolean", False, None),
        ("ValueSi", "ValueSi", "double", False, "#,0.###"),
        ("IsAbnormal", "IsAbnormal", "boolean", False, None),
        ("IsCritical", "IsCritical", "boolean", False, None),
        ("TurnaroundDays", "TurnaroundDays", "int64", False, "0"),
        ("LoadRunId", "LoadRunId", "int64", True, None),
    ]),
    ("Test", "dw", "DimTest", [
        ("TestKey", "TestKey", "int64", True, None),
        ("TestName", "TestName", "string", False, None),
        ("LoincCode", "LoincCode", "string", False, None),
        ("SiUnit", "SiUnit", "string", False, None),
        ("MapStatus", "MapStatus", "string", False, None),
        ("IsPanel", "IsPanel", "boolean", False, None),
    ]),
    ("Payer", "dw", "DimPayer", [
        ("PayerKey", "PayerKey", "int64", True, None),
        ("PayerName", "PayerName", "string", False, None),
        ("PayerType", "PayerType", "string", False, None),
    ]),
    ("Patient", "dw", "DimPatient", [
        ("PatientKey", "PatientKey", "int64", True, None),
        ("Sex", "Sex", "string", False, None),
    ]),
    ("Date", "dw", "DimDate", [
        ("DateKey", "DateKey", "int64", True, None),
        ("Date", "Date", "dateTime", False, "yyyy-mm-dd"),
        ("Year", "Year", "int64", False, "0"),
        ("Month", "Month", "int64", True, "0"),
        ("MonthName", "MonthName", "string", False, None),
        ("WeekdayNo", "WeekdayNo", "int64", True, "0"),
        ("WeekdayName", "WeekdayName", "string", False, None),
        ("IsWeekend", "IsWeekend", "boolean", False, None),
    ]),
    ("Load Runs", "etl", "SourceLoadRun", [
        ("RunId", "RunId", "int64", False, "0"),
        ("Package", "Package", "string", False, None),
        ("StartedAt", "StartedAt", "dateTime", False, "yyyy-mm-dd hh:nn"),
        ("Status", "Status", "string", False, None),
        ("ResultsExtracted", "ResultsExtracted", "int64", False, "#,0"),
        ("ResultsInserted", "ResultsInserted", "int64", False, "#,0"),
        ("ResultsUpdated", "ResultsUpdated", "int64", False, "#,0"),
        ("ResultsSkipped", "ResultsSkipped", "int64", False, "#,0"),
        ("ResultsRejected", "ResultsRejected", "int64", False, "#,0"),
        ("Warnings", "Warnings", "int64", False, "#,0"),
    ]),
    ("Data Quality Issues", "etl", "SourceRejectRow", [
        ("RejectRowId", "RejectRowId", "int64", True, None),
        ("RunId", "RunId", "int64", True, None),
        ("Severity", "Severity", "string", False, None),
        ("SourceTable", "SourceTable", "string", False, None),
        ("SourceKey", "SourceKey", "string", False, None),
        ("Rule", "Rule_", "string", False, None),
    ]),
    ("Copy Runs", "etl", "CopyRun", [
        ("CopyRunId", "CopyRunId", "int64", True, None),
        ("StartedAt", "StartedAt", "dateTime", False, "yyyy-mm-dd hh:nn"),
        ("FromSourceRunId", "FromSourceRunId", "int64", False, "0"),
        ("ToSourceRunId", "ToSourceRunId", "int64", False, "0"),
        ("FactsStaged", "FactsStaged", "int64", False, "#,0"),
        ("FactsInserted", "FactsInserted", "int64", False, "#,0"),
        ("FactsUpdated", "FactsUpdated", "int64", False, "#,0"),
    ]),
]

# (table, name, DAX, formatString, description)
MEASURES = [
    ("Results", "Result Count", "COUNTROWS ( Results )", "#,0", "Test results (panel members, not panel headers)."),
    ("Results", "Visits", "DISTINCTCOUNT ( Results[VisitNo] )", "#,0", "Distinct legacy visits."),
    ("Results", "Patients", "DISTINCTCOUNT ( Results[PatientKey] )", "#,0", "Distinct de-identified patients."),
    ("Results", "Answered Results", "CALCULATE ( [Result Count], Results[HasAnswer] = TRUE () )", "#,0", "Results with a reported value."),
    ("Results", "Answered %", "DIVIDE ( [Answered Results], [Result Count] )", "0.0%", "Share of results that have a value."),
    ("Results", "Abnormal Results", "CALCULATE ( [Result Count], Results[IsAbnormal] = TRUE () )", "#,0", "Answered results outside the reference range."),
    ("Results", "Abnormal %", "DIVIDE ( [Abnormal Results], [Answered Results] )", "0.0%", "Abnormal results over answered results."),
    ("Results", "Critical Results", "CALCULATE ( [Result Count], Results[IsCritical] = TRUE () )", "#,0", "Results flagged critical."),
    ("Results", "Avg Turnaround (days)", "AVERAGE ( Results[TurnaroundDays] )", "0.00", "Visit date to answer date, in days."),
    ("Results", "Results With Turnaround", "COUNT ( Results[TurnaroundDays] )", "#,0", "Results that have both a visit and an answer date."),
    ("Results", "Same-day %", "DIVIDE ( CALCULATE ( [Results With Turnaround], Results[TurnaroundDays] = 0 ), [Results With Turnaround] )", "0.0%", "Results answered on the visit date."),
    ("Results", "LOINC Mapped %", "DIVIDE ( CALCULATE ( [Result Count], Test[MapStatus] <> \"Unmapped\" ), [Result Count] )", "0.0%", "Results whose legacy test is mapped to a LOINC code."),
    ("Data Quality Issues", "Issue Rows", "COUNTROWS ( 'Data Quality Issues' )", "#,0", "Reject/warning rows across all ETL runs."),
    ("Data Quality Issues", "Distinct Issues", "COUNTROWS ( SUMMARIZE ( 'Data Quality Issues', 'Data Quality Issues'[SourceTable], 'Data Quality Issues'[SourceKey], 'Data Quality Issues'[Rule] ) )", "#,0", "Each source row and rule counted once, however many runs saw it."),
    ("Load Runs", "Load Runs Succeeded", "CALCULATE ( COUNTROWS ( 'Load Runs' ), 'Load Runs'[Status] = \"Succeeded\" )", "#,0", "SSIS runs that finished."),
    ("Copy Runs", "Facts Copied To Azure", "SUM ( 'Copy Runs'[FactsInserted] ) + SUM ( 'Copy Runs'[FactsUpdated] )", "#,0", "Fact rows inserted or updated in Azure by the ADF pipeline."),
]

CALC_COLUMNS = {
    "Results": [
        ("TAT Band Order", "int64", True, None,
         "SWITCH ( TRUE (), ISBLANK ( Results[TurnaroundDays] ), 9, Results[TurnaroundDays] = 0, 1, Results[TurnaroundDays] = 1, 2, Results[TurnaroundDays] <= 3, 3, Results[TurnaroundDays] <= 7, 4, 5 )"),
        ("TAT Band", "string", False, "TAT Band Order",
         "SWITCH ( Results[TAT Band Order], 1, \"Same day\", 2, \"1 day\", 3, \"2-3 days\", 4, \"4-7 days\", 5, \"8+ days\", \"No answer date\" )"),
    ],
}

# (from table, from column, to table, to column, active)
RELATIONSHIPS = [
    ("Results", "TestKey", "Test", "TestKey", True),
    ("Results", "PatientKey", "Patient", "PatientKey", True),
    ("Results", "PayerKey", "Payer", "PayerKey", True),
    ("Results", "VisitDateKey", "Date", "DateKey", True),
    ("Results", "ResultDateKey", "Date", "DateKey", False),
    ("Data Quality Issues", "RunId", "Load Runs", "RunId", True),
]


def tmdl_table(name, schema, source, columns) -> str:
    out = [f"table {q(name)}", ""]
    for t, m, dax, fmt, desc in MEASURES:
        if t == name:
            out += [f"\t/// {desc}", f"\tmeasure {q(m)} = {dax}", f"\t\tformatString: {fmt}", ""]
    for col, src, dtype, hidden, fmt in columns:
        out.append(f"\tcolumn {q(col)}")
        out.append(f"\t\tdataType: {dtype}")
        if fmt:
            out.append(f"\t\tformatString: {fmt}")
        if hidden:
            out.append("\t\tisHidden")
        out.append("\t\tsummarizeBy: none")
        out.append(f"\t\tsourceColumn: {src}")
        out.append("")
    for col, dtype, hidden, sort_by, dax in CALC_COLUMNS.get(name, []):
        out.append(f"\tcolumn {q(col)} = {dax}")
        out.append(f"\t\tdataType: {dtype}")
        if hidden:
            out.append("\t\tisHidden")
        out.append("\t\tsummarizeBy: none")
        if sort_by:
            out.append(f"\t\tsortByColumn: {q(sort_by)}")
        out.append("")
    cols = ", ".join(f'"{src}"' for _, src, *_ in columns)
    renames = [(src, col) for col, src, *_ in columns if col != src]
    m = [
        "let",
        "    Source = Sql.Database(Server, Database),",
        f'    Data = Source{{[Schema="{schema}", Item="{source}"]}}[Data],',
        f"    Kept = Table.SelectColumns(Data, {{{cols}}})" + ("," if renames else ""),
    ]
    if renames:
        pairs = ", ".join(f'{{"{a}", "{b}"}}' for a, b in renames)
        m.append(f"    Renamed = Table.RenameColumns(Kept, {{{pairs}}})")
    m += ["in", "    Renamed" if renames else "    Kept"]
    out.append(f"\tpartition {q(name)} = m")
    out.append("\t\tmode: import")
    out.append("\t\tsource =")
    out += ["\t\t\t\t" + line for line in m]
    out.append("")
    return "\n".join(out)


def build_model() -> None:
    write_json(SM / "definition.pbism", {
        "$schema": f"{SCHEMA}/item/semanticModel/definitionProperties/1.0.0/schema.json",
        "version": "4.0", "settings": {}})
    d = SM / "definition"
    write(d / "database.tmdl", "database\n\tcompatibilityLevel: 1567\n")
    order = ["Server", "Database"] + [t[0] for t in TABLES]
    write(d / "model.tmdl", "\n".join([
        "model Model",
        "\tculture: en-US",
        "\tdefaultPowerBIDataSourceVersion: powerBI_V3",
        "\tdiscourageImplicitMeasures",
        "\tsourceQueryCulture: en-US",
        "\tdataAccessOptions",
        "\t\tlegacyRedirects",
        "\t\treturnErrorValuesAsNull",
        "",
        "annotation PBI_QueryOrder = " + json.dumps(order),
        "",
        *[f"ref table {q(t[0])}" for t in TABLES],
        "",
    ]))
    write(d / "expressions.tmdl", "\n".join([
        f'expression Server = "{SERVER}" meta [IsParameterQuery=true, Type="Text", IsParameterQueryRequired=true]',
        "",
        f'expression Database = "{DATABASE}" meta [IsParameterQuery=true, Type="Text", IsParameterQueryRequired=true]',
        "",
    ]))
    rel = []
    for ft, fc, tt, tc, active in RELATIONSHIPS:
        rid = hashlib.md5(f"{ft}.{fc}->{tt}.{tc}".encode()).hexdigest()
        rel += [f"relationship {rid}"]
        if not active:
            rel.append("\tisActive: false")
        rel += [f"\tfromColumn: {q(ft)}.{q(fc)}", f"\ttoColumn: {q(tt)}.{q(tc)}", ""]
    write(d / "relationships.tmdl", "\n".join(rel))
    for t in TABLES:
        write(d / "tables" / f"{t[0]}.tmdl", tmdl_table(*t))


# ----------------------------------------------------------------- report

def vid(*parts) -> str:
    return hashlib.sha1("|".join(map(str, parts)).encode()).hexdigest()[:20]


def lit(value: str) -> dict:
    return {"expr": {"Literal": {"Value": value}}}


def field(ref: str) -> tuple[dict, str, str]:
    """'Table[Column]' or 'Table.[Measure]' -> (field, queryRef, nativeQueryRef)."""
    if ".[" in ref:
        table, prop = ref.split(".[")
        kind = "Measure"
    else:
        table, prop = ref.split("[")
        kind = "Column"
    prop = prop.rstrip("]")
    return ({kind: {"Expression": {"SourceRef": {"Entity": table}}, "Property": prop}},
            f"{table}.{prop}", prop)


def visual(page, vtype, x, y, w, h, roles=None, title=None, sort=None, objects=None, z=0):
    state = {}
    for role, refs in (roles or {}).items():
        projections = []
        for r in refs:
            f, qr, nqr = field(r)
            projections.append({"field": f, "queryRef": qr, "nativeQueryRef": nqr})
        state[role] = {"projections": projections}
    v = {"visualType": vtype}
    if state:
        v["query"] = {"queryState": state}
        if sort:
            f, _, _ = field(sort[0])
            v["query"]["sortDefinition"] = {"sort": [{"field": f, "direction": sort[1]}], "isDefaultSort": True}
    if objects:
        v["objects"] = objects
    if title:
        v["visualContainerObjects"] = {"title": [{"properties": {
            "show": lit("true"), "text": lit("'" + title.replace("'", "''") + "'")}}]}
    v["drillFilterOtherVisuals"] = True
    name = vid(page, vtype, x, y)
    return name, {
        "$schema": f"{SCHEMA}/item/report/definition/visualContainer/1.0.0/schema.json",
        "name": name,
        "position": {"x": x, "y": y, "z": z, "width": w, "height": h, "tabOrder": z},
        "visual": v,
    }


def textbox(page, x, y, w, h, text, size="18pt", bold=True):
    style = {"fontSize": size}
    if bold:
        style["fontWeight"] = "bold"
    return visual(page, "textbox", x, y, w, h, objects={"general": [{"properties": {
        "paragraphs": [{"textRuns": [{"value": text, "textStyle": style}]}]}}]})


def cards(page, y, measures, h=100):
    n = len(measures)
    gap, left, width = 16, 24, 1280 - 48
    w = (width - gap * (n - 1)) // n
    return [visual(page, "card", left + i * (w + gap), y, w, h, {"Values": [m]}, z=10 + i)
            for i, m in enumerate(measures)]


THEME_FILE = "LabFlow.json"
THEME = {
    "name": "LabFlow",
    "dataColors": ["#0F6E6E", "#E07A3F", "#3B6FB6", "#B0413E", "#6A8D2F", "#8E5EA2", "#C9A227", "#5B6770"],
    "background": "#FFFFFF", "foreground": "#1F2933", "tableAccent": "#0F6E6E",
    "good": "#2E7D32", "neutral": "#C9A227", "bad": "#B0413E",
}

PAGES = []


def page(name, display, visuals):
    PAGES.append((name, display, visuals))


def build_pages() -> None:
    p = "overview"
    page(p, "Overview", [
        textbox(p, 24, 12, 1232, 48, "LabFlow lab results — de-identified legacy data in Azure SQL"),
        *cards(p, 68, ["Results.[Result Count]", "Results.[Visits]", "Results.[Patients]",
                       "Results.[Abnormal %]", "Results.[Critical Results]", "Results.[Avg Turnaround (days)]"]),
        visual(p, "clusteredColumnChart", 24, 186, 608, 250,
               {"Category": ["Date[Date]"], "Y": ["Results.[Result Count]"]}, "Results by visit date", z=20),
        visual(p, "clusteredBarChart", 648, 186, 608, 250,
               {"Category": ["Results[AgeBand]"], "Y": ["Results.[Result Count]"]}, "Results by age band",
               sort=("Results[AgeBand]", "Ascending"), z=21),
        visual(p, "donutChart", 24, 452, 400, 250,
               {"Category": ["Payer[PayerType]"], "Y": ["Results.[Result Count]"]}, "Insured vs self-pay", z=22),
        visual(p, "clusteredColumnChart", 440, 452, 400, 250,
               {"Category": ["Patient[Sex]"], "Y": ["Results.[Abnormal %]"]}, "Abnormal % by sex", z=23),
        visual(p, "slicer", 856, 452, 400, 250, {"Values": ["Results[AgeBand]"]}, "Age band", z=24),
    ])

    p = "turnaround"
    page(p, "Turnaround", [
        textbox(p, 24, 12, 1232, 48, "Turnaround time — visit date to answer date"),
        *cards(p, 68, ["Results.[Avg Turnaround (days)]", "Results.[Same-day %]",
                       "Results.[Results With Turnaround]", "Results.[Answered %]"]),
        visual(p, "clusteredColumnChart", 24, 186, 608, 250,
               {"Category": ["Results[TAT Band]"], "Y": ["Results.[Result Count]"]}, "Results by turnaround band",
               sort=("Results[TAT Band]", "Ascending"), z=20),
        visual(p, "lineChart", 648, 186, 608, 250,
               {"Category": ["Date[Date]"], "Y": ["Results.[Avg Turnaround (days)]"]},
               "Average turnaround by visit date", z=21),
        visual(p, "tableEx", 24, 452, 1232, 250,
               {"Values": ["Test[TestName]", "Results.[Results With Turnaround]",
                           "Results.[Avg Turnaround (days)]", "Results.[Same-day %]"]},
               "Turnaround by test", sort=("Results.[Results With Turnaround]", "Descending"), z=22),
    ])

    p = "abnormal"
    page(p, "Abnormal results", [
        textbox(p, 24, 12, 1232, 48, "Abnormal and critical results"),
        *cards(p, 68, ["Results.[Answered Results]", "Results.[Abnormal Results]",
                       "Results.[Abnormal %]", "Results.[Critical Results]"]),
        visual(p, "tableEx", 24, 186, 720, 516,
               {"Values": ["Test[TestName]", "Test[LoincCode]", "Results.[Answered Results]",
                           "Results.[Abnormal Results]", "Results.[Abnormal %]", "Results.[Critical Results]"]},
               "Abnormal results by test", sort=("Results.[Abnormal Results]", "Descending"), z=20),
        visual(p, "clusteredColumnChart", 760, 186, 496, 250,
               {"Category": ["Results[AgeBand]"], "Series": ["Patient[Sex]"], "Y": ["Results.[Abnormal %]"]},
               "Abnormal % by age band and sex", sort=("Results[AgeBand]", "Ascending"), z=21),
        visual(p, "clusteredBarChart", 760, 452, 496, 250,
               {"Category": ["Payer[PayerType]"], "Y": ["Results.[Abnormal %]"]}, "Abnormal % by payer type", z=22),
    ])

    p = "quality"
    page(p, "Data quality", [
        textbox(p, 24, 12, 1232, 48, "Data quality and pipeline runs (SSIS on-prem → ADF → Azure SQL)"),
        *cards(p, 68, ["Data Quality Issues.[Distinct Issues]", "Results.[Answered %]",
                       "Results.[LOINC Mapped %]", "Copy Runs.[Facts Copied To Azure]"]),
        visual(p, "tableEx", 24, 186, 1232, 200,
               {"Values": ["Load Runs[RunId]", "Load Runs[StartedAt]", "Load Runs[Status]",
                           "Load Runs[ResultsExtracted]", "Load Runs[ResultsInserted]", "Load Runs[ResultsUpdated]",
                           "Load Runs[ResultsSkipped]", "Load Runs[ResultsRejected]", "Load Runs[Warnings]"]},
               "SSIS load runs (on-prem)", sort=("Load Runs[RunId]", "Descending"), z=20),
        visual(p, "tableEx", 24, 402, 600, 300,
               {"Values": ["Data Quality Issues[Severity]", "Data Quality Issues[Rule]",
                           "Data Quality Issues.[Distinct Issues]"]},
               "Data-quality rules hit", sort=("Data Quality Issues.[Distinct Issues]", "Descending"), z=21),
        visual(p, "tableEx", 640, 402, 616, 140,
               {"Values": ["Copy Runs[StartedAt]", "Copy Runs[FromSourceRunId]", "Copy Runs[ToSourceRunId]",
                           "Copy Runs[FactsStaged]", "Copy Runs[FactsInserted]", "Copy Runs[FactsUpdated]"]},
               "ADF copy runs (Azure)", sort=("Copy Runs[StartedAt]", "Descending"), z=22),
        visual(p, "clusteredBarChart", 640, 558, 616, 144,
               {"Category": ["Test[MapStatus]"], "Y": ["Results.[Result Count]"]}, "Results by LOINC mapping status",
               z=23),
    ])


def build_report() -> None:
    write_json(RP / "definition.pbir", {
        "$schema": f"{SCHEMA}/item/report/definitionProperties/2.0.0/schema.json",
        "version": "4.0",
        "datasetReference": {"byPath": {"path": "../LabFlow.SemanticModel"}}})
    d = RP / "definition"
    write_json(d / "version.json", {
        "$schema": f"{SCHEMA}/item/report/definition/versionMetadata/1.0.0/schema.json", "version": "2.0.0"})
    write_json(d / "report.json", {
        "$schema": f"{SCHEMA}/item/report/definition/report/1.0.0/schema.json",
        "themeCollection": {"customTheme": {
            "name": THEME_FILE, "reportVersionAtImport": "5.55", "type": "RegisteredResources"}},
        "layoutOptimization": "None",
        "resourcePackages": [{"name": "RegisteredResources", "type": "RegisteredResources", "items": [
            {"name": THEME_FILE, "path": THEME_FILE, "type": "CustomTheme"}]}],
        "settings": {"useStylableVisualContainerHeader": True, "defaultDrillFilterOtherVisuals": True}})
    write_json(RP / "StaticResources" / "RegisteredResources" / THEME_FILE, THEME)
    build_pages()
    write_json(d / "pages" / "pages.json", {
        "$schema": f"{SCHEMA}/item/report/definition/pagesMetadata/1.0.0/schema.json",
        "pageOrder": [n for n, _, _ in PAGES], "activePageName": PAGES[0][0]})
    for name, display, visuals in PAGES:
        pd = d / "pages" / name
        write_json(pd / "page.json", {
            "$schema": f"{SCHEMA}/item/report/definition/page/1.0.0/schema.json",
            "name": name, "displayName": display, "displayOption": "FitToPage", "height": 720, "width": 1280})
        for vname, body in visuals:
            write_json(pd / "visuals" / vname / "visual.json", body)


def main() -> None:
    for folder in (SM / "definition", RP / "definition", RP / "StaticResources"):
        shutil.rmtree(folder, ignore_errors=True)
    build_model()
    build_report()
    write_json(HERE / "LabFlow.pbip", {
        "$schema": f"{SCHEMA}/pbip/pbipProperties/1.0.0/schema.json",
        "version": "1.0", "artifacts": [{"report": {"path": "LabFlow.Report"}}],
        "settings": {"enableAutoRecovery": True}})
    write(HERE / ".gitignore", "**/.pbi/localSettings.json\n**/.pbi/cache.abf\n.schemas/\n")
    print(f"PBIP written to {HERE}: {len(TABLES)} tables, {len(MEASURES)} measures, {len(PAGES)} pages")


if __name__ == "__main__":
    main()
