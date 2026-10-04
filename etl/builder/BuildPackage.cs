// Builds etl/LabFlowETL/LabFlowETL.dtsx with the SSIS object model (SQL Server
// 2019 / v15), so the package is reproducible from source and reviewable as
// code. The generated .dtsx opens in Visual Studio's SSIS designer like any
// hand-drawn package.
//
//   Control flow:  SQL Start run -> DFT Extract legacy -> SQL Transform and load
//                  (OnError: SQL Fail run)
//   Data flow:     four independent paths, legacy -> etl.stg_*
//                    Tests / Payers:  OLE DB Source -> Data Conversion (code page 1256 -> Unicode) -> OLE DB Destination
//                    Visits / Results: OLE DB Source (de-identifying query from a variable) -> Row Count -> OLE DB Destination
//
// Build: etl/build.ps1
using System;
using System.Collections.Generic;
using Microsoft.SqlServer.Dts.Runtime;
using Microsoft.SqlServer.Dts.Pipeline.Wrapper;
using Microsoft.SqlServer.Dts.Tasks.ExecuteSQLTask;
using DataType = Microsoft.SqlServer.Dts.Runtime.Wrapper.DataType;

static class BuildPackage
{
    static Application app = new Application();

    static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("usage: BuildPackage <out.dtsx>"); return 2; }

        var pkg = new Package
        {
            Name = "LabFlowETL",
            Description = "Legacy Laboratory database -> de-identified staging -> LabFlow reporting warehouse (dw).",
            ProtectionLevel = DTSProtectionLevel.DontSaveSensitive,
        };

        // ---- parameters (overridable per environment: dtexec /Par, SQL Agent, catalog) ----
        AddParam(pkg, "LegacyServer", ".");
        AddParam(pkg, "LegacyDatabase", "Laboratory");
        AddParam(pkg, "LabFlowServer", ".");
        AddParam(pkg, "LabFlowDatabase", "LabFlow");
        var lookback = pkg.Parameters.Add("LookbackVisits", TypeCode.Int32);
        lookback.Value = 200;
        lookback.Description = "Re-extract this many visits below the high-water mark, to pick up answers entered after the visit.";

        // ---- connections ----
        var legacy = AddOleDb(pkg, "Legacy", "LegacyServer", "LegacyDatabase");
        var labflow = AddOleDb(pkg, "LabFlow", "LabFlowServer", "LabFlowDatabase");

        // ---- variables ----
        pkg.Variables.Add("RunId", false, "User", 0);
        pkg.Variables.Add("HashSalt", false, "User", "");
        pkg.Variables.Add("FromVisitNo", false, "User", 0);
        pkg.Variables.Add("RowsVisits", false, "User", 0);
        pkg.Variables.Add("RowsResults", false, "User", 0);

        // De-identification happens in the extract query itself: only these
        // columns ever leave the legacy database, and the patient code leaves
        // as a salted SHA-256 hash.
        AddExpressionVariable(pkg, "SqlVisits",
            "\"SELECT m.FishNo AS LegacyVisitNo, " +
            "CAST(CONVERT(char(64), HASHBYTES('SHA2_256', CONCAT('\" + @[User::HashSalt] + \"', '|', m.PRV_Code)), 2) AS nchar(16)) AS PatientHash, " +
            "p.Sex AS LegacySex, m.Age, m.AgeKind, " +
            "CAST(m.[Date] AS nvarchar(10)) AS VisitDateJ, CAST(m.[Time] AS nvarchar(8)) AS VisitTime, " +
            "CAST(m.AnsDate AS nvarchar(10)) AS AnswerDateJ, CAST(m.AnsTime AS nvarchar(8)) AS AnswerTime, " +
            "m.Bime_ID AS LegacyPayerId, m.Cancel AS Cancelled, m.PregnancyWeek " +
            "FROM dbo.MRJ m LEFT JOIN dbo.PRV p ON p.Code = m.PRV_Code " +
            "WHERE m.FishNo > \" + (DT_WSTR, 12) @[User::FromVisitNo]");
        AddExpressionVariable(pkg, "SqlResults",
            "\"SELECT r.FishNo AS LegacyVisitNo, r.Az_ID AS LegacyAzId, r.ChildAz_ID AS LegacyChildAzId, " +
            "r.[Row] AS RowNo, r.Row2 AS RowNo2, CAST(r.Answer1 AS nvarchar(250)) AS Answer, CAST(r.Unit AS nvarchar(15)) AS Unit, " +
            "CAST(r.NormalRange AS nvarchar(200)) AS NormalRange, r.OutOfNormalRange AS OutOfRange, " +
            "r.OutOfCriticalNormalRange AS OutOfCritical, CAST(r.NoAnswer AS nvarchar(1)) AS NoAnswer " +
            "FROM dbo.MRJAZ r WHERE r.FishNo > \" + (DT_WSTR, 12) @[User::FromVisitNo]");

        // ---- 1. start run ----
        var start = AddSqlTask(pkg.Executables, "SQL Start run", labflow,
            "EXEC etl.usp_StartRun @Package = 'LabFlowETL', @LookbackVisits = 200",
            "\"EXEC etl.usp_StartRun @Package = 'LabFlowETL', @LookbackVisits = \" + (DT_WSTR, 10) @[$Package::LookbackVisits]");
        var startSql = (ExecuteSQLTask)start.InnerObject;
        startSql.ResultSetType = ResultSetType.ResultSetType_SingleRow;
        Bind(startSql, "RunId", "User::RunId");
        Bind(startSql, "HashSalt", "User::HashSalt");
        Bind(startSql, "FromVisitNo", "User::FromVisitNo");

        // ---- 2. extract ----
        var dft = (TaskHost)pkg.Executables.Add("STOCK:PipelineTask");
        dft.Name = "DFT Extract legacy";
        dft.Description = "Legacy rows into etl.stg_* (truncated by SQL Start run).";
        var pipe = (MainPipe)dft.InnerObject;

        // Tests and payers: names are Persian in code page 1256; the Data
        // Conversion makes them Unicode for the nvarchar staging columns.
        {
            var src = AddSource(pipe, "SRC Legacy tests", legacy,
                "SELECT ID AS LegacyAzId, InternationalCode AS CodeRaw, Name AS NameRaw, Unit AS UnitRaw, Section FROM dbo.AzDefine", null);
            var dc = AddDataConversion(pipe, "DC Tests to Unicode", src, new[] {
                Tuple.Create("CodeRaw", "LegacyCode", 12), Tuple.Create("NameRaw", "LegacyName", 50), Tuple.Create("UnitRaw", "LegacyUnit", 15) });
            AddDestination(pipe, "DST stg_Test", labflow, "[etl].[stg_Test]", dc);
        }
        {
            var src = AddSource(pipe, "SRC Legacy payers", legacy,
                "SELECT ID AS LegacyPayerId, Name AS NameRaw FROM dbo.Bime", null);
            var dc = AddDataConversion(pipe, "DC Payers to Unicode", src, new[] { Tuple.Create("NameRaw", "PayerName", 200) });
            AddDestination(pipe, "DST stg_Payer", labflow, "[etl].[stg_Payer]", dc);
        }
        {
            var src = AddSource(pipe, "SRC Legacy visits (de-identified)", legacy, Evaluate(pkg, "SqlVisits"), "User::SqlVisits");
            var rc = AddRowCount(pipe, "RC Visits", src, "User::RowsVisits");
            AddDestination(pipe, "DST stg_Visit", labflow, "[etl].[stg_Visit]", rc);
        }
        {
            var src = AddSource(pipe, "SRC Legacy results", legacy, Evaluate(pkg, "SqlResults"), "User::SqlResults");
            var rc = AddRowCount(pipe, "RC Results", src, "User::RowsResults");
            AddDestination(pipe, "DST stg_Result", labflow, "[etl].[stg_Result]", rc);
        }

        // ---- 3. transform and load ----
        var load = AddSqlTask(pkg.Executables, "SQL Transform and load", labflow,
            "EXEC etl.usp_TransformLoad @RunId = 0",
            "\"EXEC etl.usp_TransformLoad @RunId = \" + (DT_WSTR, 12) @[User::RunId]");
        load.Description = "Map to LOINC/SI, shift dates, flag data-quality issues, MERGE into dw.";

        pkg.PrecedenceConstraints.Add(start, dft);
        pkg.PrecedenceConstraints.Add(dft, load);

        // ---- on error: close the run as Failed ----
        var onError = (DtsEventHandler)pkg.EventHandlers.Add("OnError");
        AddSqlTask(onError.Executables, "SQL Fail run", labflow,
            "EXEC etl.usp_FailRun @RunId = 0, @Message = N''",
            "\"EXEC etl.usp_FailRun @RunId = \" + (DT_WSTR, 12) @[User::RunId] + \", @Message = N'\" + " +
            "REPLACE(LEFT(@[System::SourceName] + \": \" + @[System::ErrorDescription], 1800), \"'\", \"''\") + \"'\"");

        // ---- validate and save ----
        var events = new ConsoleEvents();
        var result = pkg.Validate(null, null, events, null);
        app.SaveToXml(args[0], pkg, null);
        Console.WriteLine("Saved {0} (validation: {1})", args[0], result);
        return result == DTSExecResult.Success ? 0 : 1;
    }

    // ------------------------------------------------------------------ helpers

    static void AddParam(Package pkg, string name, string value)
    {
        var p = pkg.Parameters.Add(name, TypeCode.String);
        p.Value = value;
    }

    static ConnectionManager AddOleDb(Package pkg, string name, string serverParam, string dbParam)
    {
        var cm = pkg.Connections.Add("OLEDB");
        cm.Name = name;
        cm.ConnectionString = "Data Source=.;Initial Catalog=" + (name == "Legacy" ? "Laboratory" : "LabFlow") +
                              ";Provider=MSOLEDBSQL;Integrated Security=SSPI;";
        cm.Properties["ConnectionString"].SetExpression(cm,
            "\"Data Source=\" + @[$Package::" + serverParam + "] + \";Initial Catalog=\" + @[$Package::" + dbParam +
            "] + \";Provider=MSOLEDBSQL;Integrated Security=SSPI;\"");
        return cm;
    }

    static void AddExpressionVariable(Package pkg, string name, string expression)
    {
        var v = pkg.Variables.Add(name, false, "User", "");
        v.EvaluateAsExpression = true;
        v.Expression = expression;
    }

    static string Evaluate(Package pkg, string name)
    {
        return (string)pkg.Variables["User::" + name].Value;
    }

    static TaskHost AddSqlTask(Executables owner, string name, ConnectionManager cm, string statement, string expression)
    {
        var host = (TaskHost)owner.Add("STOCK:SQLTask");
        host.Name = name;
        var sql = (ExecuteSQLTask)host.InnerObject;
        sql.Connection = cm.Name;
        sql.SqlStatementSourceType = SqlStatementSourceType.DirectInput;
        sql.SqlStatementSource = statement;
        host.SetExpression("SqlStatementSource", expression);
        return host;
    }

    static void Bind(ExecuteSQLTask sql, string column, string variable)
    {
        IDTSResultBinding b = (IDTSResultBinding)sql.ResultSetBindings.Add();
        b.ResultName = column;
        b.DtsVariableName = variable;
    }

    static string ClassId(string componentName)
    {
        foreach (PipelineComponentInfo info in app.PipelineComponentInfos)
            if (info.Name == componentName) return info.CreationName;
        throw new InvalidOperationException("SSIS component not installed: " + componentName);
    }

    static IDTSComponentMetaData100 NewComponent(MainPipe pipe, string componentName, string name, out CManagedComponentWrapper inst)
    {
        var c = pipe.ComponentMetaDataCollection.New();
        c.ComponentClassID = ClassId(componentName);
        inst = c.Instantiate();
        inst.ProvideComponentProperties();
        c.Name = name;
        return c;
    }

    static void UseConnection(IDTSComponentMetaData100 c, ConnectionManager cm)
    {
        c.RuntimeConnectionCollection[0].ConnectionManager = DtsConvert.GetExtendedInterface(cm);
        c.RuntimeConnectionCollection[0].ConnectionManagerID = cm.ID;
    }

    // Metadata comes from the design-time query; at run time the source reads
    // the same query from the expression variable (same columns, new values).
    static IDTSComponentMetaData100 AddSource(MainPipe pipe, string name, ConnectionManager cm, string query, string queryVariable)
    {
        CManagedComponentWrapper inst;
        var c = NewComponent(pipe, "OLE DB Source", name, out inst);
        UseConnection(c, cm);
        inst.SetComponentProperty("AccessMode", 2);   // SQL command
        inst.SetComponentProperty("SqlCommand", query);
        inst.AcquireConnections(null);
        inst.ReinitializeMetaData();
        inst.ReleaseConnections();
        if (queryVariable != null)
        {
            inst.SetComponentProperty("AccessMode", 3);   // SQL command from variable
            inst.SetComponentProperty("SqlCommandVariable", queryVariable);
        }
        return c;
    }

    static IDTSPath100 Connect(MainPipe pipe, IDTSComponentMetaData100 from, IDTSComponentMetaData100 to)
    {
        var path = pipe.PathCollection.New();
        path.AttachPathAndPropagateNotifications(from.OutputCollection[0], to.InputCollection[0]);
        return path;
    }

    static IDTSComponentMetaData100 AddDataConversion(MainPipe pipe, string name, IDTSComponentMetaData100 upstream,
                                                      Tuple<string, string, int>[] conversions)
    {
        CManagedComponentWrapper inst;
        var c = NewComponent(pipe, "Data Conversion", name, out inst);
        Connect(pipe, upstream, c);
        var input = c.InputCollection[0];
        var vInput = input.GetVirtualInput();
        var output = c.OutputCollection[0];
        foreach (var conv in conversions)
        {
            IDTSVirtualInputColumn100 vc = null;
            foreach (IDTSVirtualInputColumn100 x in vInput.VirtualInputColumnCollection)
                if (x.Name == conv.Item1) vc = x;
            if (vc == null) throw new InvalidOperationException(name + ": no input column " + conv.Item1);
            var ic = inst.SetUsageType(input.ID, vInput, vc.LineageID, DTSUsageType.UT_READONLY);
            var oc = inst.InsertOutputColumnAt(output.ID, output.OutputColumnCollection.Count, conv.Item2, "");
            inst.SetOutputColumnDataTypeProperties(output.ID, oc.ID, DataType.DT_WSTR, conv.Item3, 0, 0, 0);
            inst.SetOutputColumnProperty(output.ID, oc.ID, "SourceInputColumnLineageID", ic.LineageID);
            inst.SetOutputColumnProperty(output.ID, oc.ID, "FastParse", false);
        }
        return c;
    }

    static IDTSComponentMetaData100 AddRowCount(MainPipe pipe, string name, IDTSComponentMetaData100 upstream, string variable)
    {
        CManagedComponentWrapper inst;
        var c = NewComponent(pipe, "Row Count", name, out inst);
        inst.SetComponentProperty("VariableName", variable);
        Connect(pipe, upstream, c);
        return c;
    }

    // Fast load into a staging table; columns map by name.
    static void AddDestination(MainPipe pipe, string name, ConnectionManager cm, string table, IDTSComponentMetaData100 upstream)
    {
        CManagedComponentWrapper inst;
        var c = NewComponent(pipe, "OLE DB Destination", name, out inst);
        UseConnection(c, cm);
        inst.SetComponentProperty("AccessMode", 3);   // table - fast load
        inst.SetComponentProperty("OpenRowset", table);
        inst.SetComponentProperty("FastLoadOptions", "TABLOCK,CHECK_CONSTRAINTS");
        inst.AcquireConnections(null);
        inst.ReinitializeMetaData();
        inst.ReleaseConnections();
        Connect(pipe, upstream, c);

        var input = c.InputCollection[0];
        var vInput = input.GetVirtualInput();
        var external = new Dictionary<string, IDTSExternalMetadataColumn100>(StringComparer.OrdinalIgnoreCase);
        foreach (IDTSExternalMetadataColumn100 e in input.ExternalMetadataColumnCollection) external[e.Name] = e;
        var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IDTSVirtualInputColumn100 vc in vInput.VirtualInputColumnCollection)
        {
            IDTSExternalMetadataColumn100 ext;
            if (!external.TryGetValue(vc.Name, out ext)) continue;   // e.g. the raw code-page-1256 columns
            var ic = inst.SetUsageType(input.ID, vInput, vc.LineageID, DTSUsageType.UT_READONLY);
            inst.MapInputColumn(input.ID, ic.ID, ext.ID);
            mapped.Add(vc.Name);
        }
        foreach (var col in external.Keys)
            if (!mapped.Contains(col)) throw new InvalidOperationException(name + ": nothing maps to " + table + "." + col);
    }

    class ConsoleEvents : DefaultEvents
    {
        public override bool OnError(DtsObject source, int errorCode, string subComponent, string description, string helpFile, int helpContext, string idofInterfaceWithError)
        {
            Console.Error.WriteLine("ERROR {0}: {1}", subComponent, description);
            return false;
        }
        public override void OnWarning(DtsObject source, int warningCode, string subComponent, string description, string helpFile, int helpContext, string idofInterfaceWithError)
        {
            Console.WriteLine("warning {0}: {1}", subComponent, description);
        }
    }
}
