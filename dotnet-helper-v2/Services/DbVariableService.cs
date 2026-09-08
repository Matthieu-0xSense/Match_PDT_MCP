using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MatchPdt.Helper.Bootstrap;
using MatchPdt.Helper.Rpc;

namespace MatchPdt.Helper.Services
{
    /// <summary>
    /// Phase 4: DB list / variable CRUD via PDT services.
    ///
    /// Verified via decompilation of Hydac.PDT.Database.Business.Contracts +
    /// DatabaseObjects (PDT 2.12.102):
    /// - <c>IDatabaseListRepository</c>: GetByEcuId / GetByEcuIdAndName / GetById /
    ///   Add / Update / Remove. Read-side fully covered.
    /// - <c>IDatabaseVariableRepository</c>: read-only (GetAll/ById/ByListId/
    ///   ByListIdAndName) — mutation goes through TRepository.
    /// - <c>IOldDatabaseVariableFactory.Create(ecuId, listName, variableType)</c>
    ///   creates and registers a TVar in the project. variableType is the
    ///   symbolic name (e.g. "TUINT8"). Returns ITVar with auto-populated
    ///   default-dataset TValues (one per <c>DatabaseList.DefaultDataSetCount</c>).
    ///
    /// add_db_variable flow:
    ///   1. Resolve IDatabaseListRepository, IOldDatabaseVariableFactory.
    ///   2. Resolve database list by (ecuId, listName) → list.Id, list.EcuId.
    ///   3. Validate variableType against project's TType repository.
    ///   4. factory.Create(ecuId, listName, variableType) → ITVar.
    ///   5. Set Name, Description, Min, Max, UnitEcu via ITVar setters.
    ///   6. For each TValue in var.Values, set Detail.Defaults to the default value.
    ///   7. host.SaveProject() → RunSaveAdapters covers DatabaseLists.dat and
    ///      project.dat (variables live in TRepository → project.dat).
    /// </summary>
    internal static class DbVariableService
    {
        // ------------------------------------------------------------------
        // CommID support.
        //
        // IOldDatabaseVariableFactory.Create leaves the variable's comm id at
        // -1. PDT needs it unique per project — it is the id the MST protocol
        // uses to read/write the variable from the service tool, so duplicates
        // (or a project full of -1) make the DB unaddressable.
        //
        // The v1 helper set "CommID" on the raw TVar; project.dat confirms the
        // member list (…TType.hstCode.CommID…) and the backing store
        // TFieldExtendedBase+u16ComID. Probe by property first, then by field,
        // so a PDT rename does not silently go back to writing nothing.
        // ------------------------------------------------------------------
        private static readonly string[] CommIdPropNames =
            { "CommID", "CommId", "ComID", "ComId", "CommunicationId" };
        private static readonly string[] CommIdFieldNames =
            { "u16ComID", "u16CommID", "CommID", "ComID" };

        private const BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>Name of the member actually used, for diagnostics.</summary>
        internal static string CommIdMemberUsed { get; private set; } = "(none)";

        private static PropertyInfo? FindCommIdProp(Type t)
        {
            foreach (var n in CommIdPropNames)
            {
                var p = t.GetProperty(n, AnyInstance);
                if (p != null && p.CanRead && p.PropertyType != typeof(string))
                    return p;
            }
            return null;
        }

        private static FieldInfo? FindCommIdField(Type t)
        {
            for (var cur = t; cur != null; cur = cur.BaseType)
                foreach (var n in CommIdFieldNames)
                {
                    var f = cur.GetField(n, AnyInstance);
                    if (f != null) return f;
                }
            return null;
        }

        private static int? GetCommId(object v)
        {
            var t = v.GetType();
            var p = FindCommIdProp(t);
            if (p != null)
            {
                CommIdMemberUsed = "property:" + p.Name;
                return Convert.ToInt32(p.GetValue(v) ?? -1);
            }
            var f = FindCommIdField(t);
            if (f == null) return null;
            CommIdMemberUsed = "field:" + f.Name;
            return Convert.ToInt32(f.GetValue(v) ?? -1);
        }

        private static bool SetCommId(object v, int id)
        {
            var t = v.GetType();
            var p = FindCommIdProp(t);
            if (p != null && p.CanWrite)
            {
                p.SetValue(v, Convert.ChangeType(id, Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType));
                CommIdMemberUsed = "property:" + p.Name;
                return true;
            }
            var f = FindCommIdField(t);
            if (f == null) return false;
            f.SetValue(v, Convert.ChangeType(id, f.FieldType));
            CommIdMemberUsed = "field:" + f.Name;
            return true;
        }

        // ------------------------------------------------------------------
        // Idx support.
        //
        // Every variable needs a 1-based position within its list. Left at the
        // factory's -1, PdtCodeBuilder aborts:
        //   [DBM] ERROR first value parameter position number ... [value = -1]
        //   [PAR] ERROR Parsing of DbListValue failed ... for DbList = DB_...
        // The repository contract only hands out read-only DatabaseVariable
        // models, so we go to projectAgent.Repository.Variables for the live
        // TVars — the same route ResolveVariable takes.
        // ------------------------------------------------------------------
        private static readonly string[] IdxPropNames = { "Idx", "Index", "Position" };

        internal static string IdxMemberUsed { get; private set; } = "(none)";

        private static PropertyInfo? FindIdxProp(Type t)
        {
            foreach (var n in IdxPropNames)
            {
                var p = t.GetProperty(n, AnyInstance);
                if (p != null && p.CanRead && p.PropertyType != typeof(string))
                    return p;
            }
            return null;
        }

        private static int GetIdx(object v)
        {
            var p = FindIdxProp(v.GetType());
            return p == null ? -1 : Convert.ToInt32(p.GetValue(v) ?? -1);
        }

        private static bool SetIdx(object v, int idx)
        {
            var p = FindIdxProp(v.GetType());
            if (p == null || !p.CanWrite) return false;
            p.SetValue(v, Convert.ChangeType(idx, Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType));
            IdxMemberUsed = "property:" + p.Name;
            return true;
        }

        /// <summary>Live TVar objects from projectAgent.Repository.Variables.</summary>
        private static List<object> LiveVariables()
        {
            var businessIfaces = Assembly.Load("Hydac.PDT.Business.Interfaces");
            var projectServiceType = businessIfaces.GetType(
                "Hydac.PDT.Business.Contracts.Project.IProjectService", throwOnError: true)!;
            var projectService = HostBootstrap.ResolveByReflection(projectServiceType)
                ?? throw new InvalidOperationException("IProjectService not registered");
            var actualAgent = projectServiceType.GetProperty("ActualProjectAgent")!.GetValue(projectService)
                ?? throw new InvalidOperationException("No project loaded");
            var repository = actualAgent.GetType().GetProperty("Repository")!.GetValue(actualAgent)!;
            var vars = (IEnumerable)repository.GetType().GetProperty("Variables")!.GetValue(repository)!;
            return vars.Cast<object>().Where(v => v != null).ToList();
        }

        private static Guid ListIdOf(object v)
        {
            var p = v.GetType().GetProperty("DatabaseListId", AnyInstance);
            var raw = p?.GetValue(v);
            if (raw is Guid g) return g;
            return Guid.TryParse(raw?.ToString(), out var parsed) ? parsed : Guid.Empty;
        }

        /// <summary>
        /// Give every variable a 1-based position inside its own list, keeping the
        /// existing order. Safe to re-run: it renumbers from scratch each time.
        /// </summary>
        public static RpcResponse RenumberIdx(HostBootstrap host, RpcRequest req)
        {
            try
            {
                var vars = LiveVariables();
                var byList = new Dictionary<Guid, List<object>>();
                foreach (var v in vars)
                {
                    if (!byList.TryGetValue(ListIdOf(v), out var bucket))
                        byList[ListIdOf(v)] = bucket = new List<object>();
                    bucket.Add(v);
                }

                int assigned = 0, failed = 0;
                var perList = new List<object>();
                foreach (var kv in byList)
                {
                    int n = 0;
                    foreach (var v in kv.Value)
                    {
                        n++;
                        if (SetIdx(v, n)) assigned++;
                        else failed++;
                    }
                    perList.Add(new { list_id = kv.Key.ToString(), count = n });
                }

                if (assigned > 0) host.SaveProject();
                Program.WriteLog($"renumber_idx: {assigned} assigned, {failed} failed, member={IdxMemberUsed}");

                return RpcResponse.Ok(req.Id, new
                {
                    status = failed == 0 ? "ok" : "partial",
                    total_variables = vars.Count,
                    lists = byList.Count,
                    assigned,
                    failed,
                    member_used = IdxMemberUsed,
                    per_list = perList,
                });
            }
            catch (Exception ex)
            {
                return RpcResponse.Fail(req.Id, RpcErrorCodes.InternalError,
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>All ITVars in the project, via IDatabaseVariableRepository.GetAll().</summary>
        private static List<object> AllVariables()
        {
            var dbContracts = Assembly.Load("Hydac.PDT.Database.Business.Contracts");
            var varRepoType = dbContracts.GetType(
                "Hydac.PDT.Database.Business.Contracts.Variables.IDatabaseVariableRepository",
                throwOnError: true)!;
            var varRepo = HostBootstrap.ResolveByReflection(varRepoType);
            if (varRepo == null) return new List<object>();
            var all = varRepoType.GetMethod("GetAll")!.Invoke(varRepo, null) as IEnumerable;
            return all == null ? new List<object>() : all.Cast<object>().Where(o => o != null).ToList();
        }

        private static int MaxCommId(IEnumerable<object> vars)
        {
            int max = 0;
            foreach (var v in vars)
            {
                var c = GetCommId(v);
                if (c.HasValue && c.Value > max) max = c.Value;
            }
            return max;
        }

        /// <summary>
        /// Give every variable whose comm id is missing or non-positive a fresh
        /// unique one. Existing positive ids are left alone so a project that is
        /// already numbered keeps its service-tool addresses stable.
        /// </summary>
        public static RpcResponse RenumberCommIds(HostBootstrap host, RpcRequest req)
        {
            try
            {
                var vars = AllVariables();
                var used = new HashSet<int>();
                var missing = new List<object>();
                foreach (var v in vars)
                {
                    var c = GetCommId(v);
                    if (c.HasValue && c.Value > 0) used.Add(c.Value);
                    else missing.Add(v);
                }

                int next = 1, assigned = 0, failed = 0;
                foreach (var v in missing)
                {
                    while (used.Contains(next)) next++;
                    if (SetCommId(v, next)) { used.Add(next); assigned++; next++; }
                    else failed++;
                }

                if (assigned > 0) host.SaveProject();
                Program.WriteLog($"renumber_comm_ids: {assigned} assigned, {failed} failed, member={CommIdMemberUsed}");

                return RpcResponse.Ok(req.Id, new
                {
                    status = failed == 0 ? "ok" : "partial",
                    total_variables = vars.Count,
                    already_numbered = vars.Count - missing.Count,
                    assigned,
                    failed,
                    max_comm_id = used.Count == 0 ? 0 : used.Max(),
                    member_used = CommIdMemberUsed,
                });
            }
            catch (Exception ex)
            {
                return RpcResponse.Fail(req.Id, RpcErrorCodes.InternalError,
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Diagnostic: full member surface of the first DB variable, so the
        /// writable comm-id path can be identified without guessing.
        /// </summary>
        public static RpcResponse ProbeVarMembers(HostBootstrap host, RpcRequest req)
        {
            try
            {
                object v;
                // mode=itvar: build a throwaway ITVar through the factory and
                // inspect that instead of the repository's read-only DTO. The
                // project is never saved here, so the temp variable dies with
                // the process.
                if (req.GetStringParam("mode") == "itvar")
                {
                    var listName = req.GetStringParam("database");
                    var symbolic = req.GetStringParam("type", "TUINT16");
                    var dbObjects2 = Assembly.Load("DatabaseObjects");
                    var businessIfaces2 = Assembly.Load("Hydac.PDT.Business.Interfaces");
                    var factoryType = dbObjects2.GetType("Hydac.PDT.ViewModel.IOldDatabaseVariableFactory", false)
                        ?? FindTypeBySimpleName("IOldDatabaseVariableFactory")!;
                    var factory = HostBootstrap.ResolveByReflection(factoryType)!;
                    var projSvcType = businessIfaces2.GetType("Hydac.PDT.Business.Contracts.Project.IProjectService", true)!;
                    var projSvc = HostBootstrap.ResolveByReflection(projSvcType)!;
                    var agent = projSvcType.GetProperty("ActualProjectAgent")!.GetValue(projSvc)!;
                    var ecus = (IEnumerable)agent.GetType().GetProperty("VirtualEcus")!.GetValue(agent)!;
                    var ecu = ecus.Cast<object>().First();
                    var ecuIdP = (Guid)ecu.GetType().GetProperty("ObjectId")!.GetValue(ecu)!;
                    var create = factoryType.GetMethods().First(m => m.Name == "Create" && m.GetParameters().Length == 3);
                    v = create.Invoke(factory, new object[] { ecuIdP, listName, symbolic })!;
                }
                else
                {
                    var vars = AllVariables();
                    if (vars.Count == 0)
                        return RpcResponse.Ok(req.Id, new { error = "no variables in project" });
                    v = vars[0];
                }
                var t = v.GetType();

                var props = t.GetProperties(AnyInstance)
                    .Select(p => $"{(p.CanWrite ? "RW" : "RO")} {p.PropertyType.Name} {p.Name}")
                    .OrderBy(s => s).ToList();

                var fields = new List<string>();
                for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
                    foreach (var f in cur.GetFields(AnyInstance | BindingFlags.DeclaredOnly))
                        fields.Add($"{cur.Name}+{f.FieldType.Name} {f.Name}");

                // Anything that smells like a comm id, with its current value.
                var candidates = new List<string>();
                foreach (var p in t.GetProperties(AnyInstance))
                    if (p.Name.IndexOf("com", StringComparison.OrdinalIgnoreCase) >= 0)
                        candidates.Add($"prop {(p.CanWrite ? "RW" : "RO")} {p.Name} = {SafeGet(() => p.GetValue(v))}");
                for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
                    foreach (var f in cur.GetFields(AnyInstance | BindingFlags.DeclaredOnly))
                        if (f.Name.IndexOf("com", StringComparison.OrdinalIgnoreCase) >= 0)
                            candidates.Add($"field {cur.Name}+{f.Name} = {SafeGet(() => f.GetValue(v))}");

                // Nested holders (Detail, Field, …) often carry the real storage.
                var nested = new List<string>();
                foreach (var p in t.GetProperties(AnyInstance))
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    if (p.PropertyType.IsPrimitive || p.PropertyType == typeof(string)) continue;
                    object? child = SafeGet(() => p.GetValue(v));
                    if (child == null) continue;
                    var ct = child.GetType();
                    foreach (var cp in ct.GetProperties(AnyInstance))
                        if (cp.Name.IndexOf("com", StringComparison.OrdinalIgnoreCase) >= 0)
                            nested.Add($"{p.Name}({ct.Name}).{cp.Name} {(cp.CanWrite ? "RW" : "RO")} = {SafeGet(() => cp.GetValue(child))}");
                    for (var cur = ct; cur != null && cur != typeof(object); cur = cur.BaseType)
                        foreach (var cf in cur.GetFields(AnyInstance | BindingFlags.DeclaredOnly))
                            if (cf.Name.IndexOf("com", StringComparison.OrdinalIgnoreCase) >= 0)
                                nested.Add($"{p.Name}({cur.Name}).field {cf.Name} = {SafeGet(() => cf.GetValue(child))}");
                }

                return RpcResponse.Ok(req.Id, new
                {
                    concrete_type = t.FullName,
                    assembly = t.Assembly.GetName().Name,
                    comm_candidates = candidates,
                    nested_comm_candidates = nested,
                    properties = props,
                    fields,
                });
            }
            catch (Exception ex)
            {
                return RpcResponse.Fail(req.Id, RpcErrorCodes.InternalError,
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static object? SafeGet(Func<object?> f)
        {
            try { return f(); } catch { return "<throws>"; }
        }

        public static RpcResponse DumpDbVariables(HostBootstrap host, RpcRequest req)
        {
            try
            {
                var dbContracts = Assembly.Load("Hydac.PDT.Database.Business.Contracts");
                var listRepoType = dbContracts.GetType(
                    "Hydac.PDT.Database.Business.Contracts.IDatabaseListRepository", throwOnError: true)!;
                var varRepoType = dbContracts.GetType(
                    "Hydac.PDT.Database.Business.Contracts.Variables.IDatabaseVariableRepository", throwOnError: true)!;

                var listRepo = HostBootstrap.ResolveByReflection(listRepoType);
                var varRepo = HostBootstrap.ResolveByReflection(varRepoType);

                var lists = (listRepo == null) ? null
                    : ((IEnumerable)listRepoType.GetMethod("GetLists")!.Invoke(listRepo, null)!).Cast<object>().ToList();
                var vars = (varRepo == null) ? null
                    : ((IEnumerable)varRepoType.GetMethod("GetAll")!.Invoke(varRepo, null)!).Cast<object>().ToList();

                static object SummarizeList(object l)
                {
                    var t = l.GetType();
                    string? str(string p) => t.GetProperty(p)?.GetValue(l)?.ToString();
                    return new
                    {
                        id = str("Id"),
                        ecu_id = str("EcuId"),
                        name = str("Name"),
                        list_type = str("DatabaseListType"),
                    };
                }

                static object SummarizeVar(object v)
                {
                    var t = v.GetType();
                    string? str(string p) => t.GetProperty(p)?.GetValue(v)?.ToString();
                    return new
                    {
                        id = str("Id"),
                        list_id = str("ListId"),
                        name = str("Name"),
                        size_byte = str("SizeInByte"),
                        min = str("Min"),
                        max = str("Max"),
                        description = str("Description"),
                    };
                }

                return RpcResponse.Ok(req.Id, new
                {
                    services_resolved = new
                    {
                        IDatabaseListRepository = listRepo?.GetType().FullName,
                        IDatabaseVariableRepository = varRepo?.GetType().FullName,
                    },
                    counts = new
                    {
                        lists = lists?.Count ?? -1,
                        variables = vars?.Count ?? -1,
                    },
                    sample_lists = lists?.Take(5).Select(SummarizeList).ToList(),
                    sample_variables = vars?.Take(5).Select(SummarizeVar).ToList(),
                });
            }
            catch (Exception ex)
            {
                return RpcResponse.Fail(req.Id, RpcErrorCodes.InternalError,
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        public static RpcResponse AddDbVariable(HostBootstrap host, RpcRequest req)
        {
            try
            {
                var listName = req.GetStringParam("database");
                var name = req.GetStringParam("name");
                var varType = req.GetStringParam("type", "TUINT8");
                var defaultVal = req.GetStringParam("default");
                var min = req.GetStringParam("min");
                var max = req.GetStringParam("max");
                var unit = req.GetStringParam("unit", "[-]");
                var description = req.GetStringParam("description");
                if (string.IsNullOrEmpty(listName)) throw new ArgumentException("'database' is required");
                if (string.IsNullOrEmpty(name)) throw new ArgumentException("'name' is required");

                // PDT stores a boolean default as 1/0; callers write TRUE/FALSE
                // because that is what the IEC source says.
                defaultVal = NormalizeDefault(varType, defaultVal);

                var dbContracts = Assembly.Load("Hydac.PDT.Database.Business.Contracts");
                var businessIfaces = Assembly.Load("Hydac.PDT.Business.Interfaces");
                var dbObjects = Assembly.Load("DatabaseObjects");

                var listRepoType = dbContracts.GetType(
                    "Hydac.PDT.Database.Business.Contracts.IDatabaseListRepository", throwOnError: true)!;
                var oldFactoryType = dbObjects.GetType(
                    "Hydac.PDT.ViewModel.IOldDatabaseVariableFactory", throwOnError: false)
                    ?? FindTypeBySimpleName("IOldDatabaseVariableFactory")
                    ?? throw new InvalidOperationException("IOldDatabaseVariableFactory type not found");

                var listRepo = HostBootstrap.ResolveByReflection(listRepoType)
                    ?? throw new InvalidOperationException("IDatabaseListRepository not registered");
                var oldFactory = HostBootstrap.ResolveByReflection(oldFactoryType)
                    ?? throw new InvalidOperationException("IOldDatabaseVariableFactory not registered");

                // Resolve database list. Use first VirtualEcu (matches v1 single-ECU
                // assumption); for multi-ECU projects extend with explicit ecu param.
                var projectServiceType = businessIfaces.GetType(
                    "Hydac.PDT.Business.Contracts.Project.IProjectService", throwOnError: true)!;
                var projectService = HostBootstrap.ResolveByReflection(projectServiceType)
                    ?? throw new InvalidOperationException("IProjectService not registered");
                var actualAgent = projectServiceType.GetProperty("ActualProjectAgent")!.GetValue(projectService)
                    ?? throw new InvalidOperationException("No project loaded");
                var virtualEcus = (IEnumerable)actualAgent.GetType().GetProperty("VirtualEcus")!.GetValue(actualAgent)!;
                var firstEcu = virtualEcus.Cast<object>().FirstOrDefault()
                    ?? throw new InvalidOperationException("Project has no VirtualEcus");
                var ecuId = (Guid)firstEcu.GetType().GetProperty("ObjectId")!.GetValue(firstEcu)!;

                var getByEcuAndName = listRepoType.GetMethod("GetByEcuIdAndName")!;
                var dbList = getByEcuAndName.Invoke(listRepo, new object[] { ecuId, listName })
                    ?? throw new ArgumentException($"Database list '{listName}' not found on ECU {ecuId}.");
                var listId = (Guid)dbList.GetType().GetProperty("Id")!.GetValue(dbList)!;

                // Uniqueness check via IDatabaseVariableRepository.
                var varRepoType = dbContracts.GetType(
                    "Hydac.PDT.Database.Business.Contracts.Variables.IDatabaseVariableRepository", throwOnError: true)!;
                var varRepo = HostBootstrap.ResolveByReflection(varRepoType)!;
                var byListIdAndName = varRepoType.GetMethod("GetByListIdAndName")!;
                var existing = byListIdAndName.Invoke(varRepo, new object[] { listId, name });
                if (existing != null)
                    throw new ArgumentException($"Variable '{name}' already exists in '{listName}'.");

                // Resolve the actual TType via IDataTypeLoader.GetByTypeName (matches
                // TType.TypeName, e.g. "TUINT8"). Read its SymbolicName for the factory
                // call — TType.SymbolicName = ($"{Prefix}{Name}").ToUpperInvariant() and
                // the 3-arg Create matches on SymbolicName, not TypeName.
                var dataTypeLoaderType = businessIfaces.GetType(
                    "Hydac.PDT.Business.Datapool.DataTransfer.IDataTypeLoader", throwOnError: true)!;
                var dtLoader = HostBootstrap.ResolveByReflection(dataTypeLoaderType)
                    ?? throw new InvalidOperationException("IDataTypeLoader not registered");
                var resolvedType = dataTypeLoaderType.GetMethod("GetByTypeName")!
                    .Invoke(dtLoader, new object[] { varType });
                if (resolvedType == null)
                    throw new ArgumentException(
                        $"Unknown 'type': '{varType}'. " +
                        $"Try TUINT8, TUINT16, TINT16, TUINT32, TFLOAT32, TBOOLEAN.");
                var symbolicName = (string?)resolvedType.GetType().GetProperty("SymbolicName")?.GetValue(resolvedType)
                    ?? throw new InvalidOperationException("TType.SymbolicName missing");
                Program.WriteLog($"add_db_variable: TType TypeName='{varType}' → SymbolicName='{symbolicName}'");

                // Create via factory's 3-arg path: registers TVar in the repository,
                // sets TType + VarType from the SymbolicName lookup, and creates a TValue
                // per default dataset on the list — exactly what we need.
                var createMethod = oldFactoryType.GetMethods()
                    .First(m => m.Name == "Create" && m.GetParameters().Length == 3);
                object? itVar;
                try
                {
                    itVar = createMethod.Invoke(oldFactory, new object[] { ecuId, listName, symbolicName });
                }
                catch (TargetInvocationException tie) when (tie.InnerException is not null)
                {
                    throw tie.InnerException;
                }
                if (itVar == null)
                    throw new InvalidOperationException(
                        $"IOldDatabaseVariableFactory.Create returned null for SymbolicName '{symbolicName}'");

                // Set ITVar properties.
                var varT = itVar.GetType();
                varT.GetProperty("Name")?.SetValue(itVar, name);
                if (!string.IsNullOrEmpty(description))
                    varT.GetProperty("Description")?.SetValue(itVar, description);
                if (!string.IsNullOrEmpty(min))
                    varT.GetProperty("Min")?.SetValue(itVar, min);
                if (!string.IsNullOrEmpty(max))
                    varT.GetProperty("Max")?.SetValue(itVar, max);
                if (!string.IsNullOrEmpty(unit))
                    varT.GetProperty("UnitEcu")?.SetValue(itVar, unit);

                // Set default value in each ITValue's Detail.Defaults.
                if (!string.IsNullOrEmpty(defaultVal))
                {
                    SetIsArray(itVar, SplitDefault(defaultVal).Length > 1);
                    var values = (IEnumerable)varT.GetProperty("Values")!.GetValue(itVar)!;
                    int valueCount = 0;
                    foreach (var v in values)
                    {
                        SetDefaultOnTValue(v, defaultVal);
                        valueCount++;
                    }
                    Program.WriteLog($"add_db_variable: set Default='{defaultVal}' on {valueCount} TValue(s)");
                }

                var varId = (Guid)varT.GetProperty("GUID")!.GetValue(itVar)!;

                // The factory leaves the comm id at -1; PDT needs it unique per
                // project. Take the highest in use and add one.
                int commId = MaxCommId(AllVariables()) + 1;
                bool commOk = SetCommId(itVar, commId);
                if (!commOk)
                    Program.WriteLog("add_db_variable: WARNING could not set comm id — no known member on "
                                     + varT.FullName);

                // Position inside the list. Left at -1 the code builder rejects the
                // whole DbList ("first value parameter position number ... [value = -1]").
                int maxIdx = 0;
                foreach (var other in LiveVariables())
                {
                    if (!ReferenceEquals(other, itVar) && ListIdOf(other) == listId)
                        maxIdx = Math.Max(maxIdx, GetIdx(other));
                }
                bool idxOk = SetIdx(itVar, maxIdx + 1);
                if (!idxOk)
                    Program.WriteLog("add_db_variable: WARNING could not set idx on " + varT.FullName);

                Program.WriteLog($"add_db_variable: created '{name}' (id={varId}) in list '{listName}' (id={listId}) as {varType}, commId={(commOk ? commId.ToString() : "unset")}, idx={(idxOk ? (maxIdx + 1).ToString() : "unset")}");

                host.SaveProject();

                return RpcResponse.Ok(req.Id, new
                {
                    status = "ok",
                    message = $"Created variable '{name}' in '{listName}' as {varType}",
                    comm_id = commOk ? commId : -1,
                    name,
                    type = varType,
                    @default = defaultVal,
                    var_id = varId.ToString(),
                    list_id = listId.ToString(),
                });
            }
            catch (Exception ex)
            {
                Program.WriteLog($"add_db_variable failed: {ex}");
                return RpcResponse.Fail(req.Id, RpcErrorCodes.InternalError,
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Mutate ITValue.Detail.Defaults to contain a single TDefaultValueDetail with
        /// the requested default. The Defaults collection holds one ITDefaultValueDetail
        /// per character of the default (PDT stores numeric defaults as digit strings —
        /// "100" → 3 entries 1, 0, 0). Easier to clear + use ITDefaultValueDetailFactory
        /// per char.
        /// </summary>
        /// <summary>
        /// Map a caller-supplied default onto what the per-character default store
        /// accepts. Only TBOOLEAN needs translating today: callers naturally write
        /// TRUE/FALSE (that is what the IEC source says), PDT wants 1/0.
        /// </summary>
        private static string NormalizeDefault(string varType, string defaultVal)
        {
            if (!string.Equals(varType, "TBOOLEAN", StringComparison.OrdinalIgnoreCase))
                return defaultVal;
            if (string.IsNullOrWhiteSpace(defaultVal))
                return "0";

            switch (defaultVal.Trim().ToUpperInvariant())
            {
                case "TRUE": case "1": case "YES": case "ON":
                    return "1";
                case "FALSE": case "0": case "NO": case "OFF":
                    return "0";
                default:
                    throw new ArgumentException(
                        $"'{defaultVal}' is not a valid TBOOLEAN default; use TRUE/FALSE or 1/0.");
            }
        }

        private static void SetDefaultOnTValue(object tValue, string defaultStr)
        {
            var detail = tValue.GetType().GetProperty("Detail")?.GetValue(tValue);
            if (detail == null) return;
            var defaults = detail.GetType().GetProperty("Defaults")?.GetValue(detail) as IList;
            if (defaults == null) return;

            // Resolve ITDefaultValueDetailFactory for fresh entries.
            var dlIface = Assembly.Load("Hydac.PDT.DataLayer.Interface");
            var factoryType = dlIface.GetType("Hydac.PDT.DataLayer.Interface.ITDefaultValueDetailFactory", throwOnError: false)
                ?? FindTypeBySimpleName("ITDefaultValueDetailFactory");
            if (factoryType == null) return;
            var factory = HostBootstrap.ResolveByReflection(factoryType);
            if (factory == null) return;
            var createOne = factoryType.GetMethods().FirstOrDefault(m => m.Name == "Create" && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typeof(string));
            if (createOne == null) return;

            // One entry holding the whole value. The previous per-character loop
            // was based on a misreading of the store: PDT validates every entry in
            // Defaults as a complete value, so "FALSE" produced five errors (F, A,
            // L, S, E) and "600000" silently became a default of 6.
            // An array default arrives as "[a,b,c]" and becomes one entry per element: that
            // is how PDT stores arrays (hIndexWeights carries seven entries), the element
            // count being the array length. A scalar is the one-element case.
            defaults.Clear();
            foreach (var element in SplitDefault(defaultStr))
            {
                var entry = createOne.Invoke(factory, new object[] { element });
                if (entry != null) defaults.Add(entry);
            }
        }

        /// <summary>"[1, 2, 3]" → {"1","2","3"}; anything else → {itself}.</summary>
        /// <summary>PDT refuses longer arrays in its own validation (DatabaseVariableArrayLengthValidationAlgorithm).</summary>
        internal const int MaxArrayLength = 31;

        internal static string[] SplitDefault(string defaultStr)
        {
            var t = defaultStr.Trim();
            if (t.Length >= 2 && t[0] == '[' && t[t.Length - 1] == ']')
            {
                var elements = t.Substring(1, t.Length - 2).Split(',').Select(x => x.Trim()).ToArray();
                if (elements.Length > MaxArrayLength)
                    throw new ArgumentException(
                        $"Array default has {elements.Length} elements; PDT allows at most {MaxArrayLength}. Split it.");
                return elements;
            }
            return new[] { t };
        }

        /// <summary>Flag the variable as an array when its default carries more than one element.</summary>
        internal static void SetIsArray(object itVar, bool isArray)
        {
            // project.dat stores this as TVar.gcVarFunction, one byte: 0 scalar, 1 array (EVarFunc.VArray).
            // VarFunctionByte is the public setter over it on both ITVar and TVar; IsArray on the
            // class is read-only, which is why an earlier probe on it changed nothing.
            var t = itVar.GetType();
            var pb = t.GetProperty("VarFunctionByte", AnyInstance);
            if (pb != null && pb.CanWrite)
            {
                pb.SetValue(itVar, Convert.ChangeType(isArray ? 1 : 0, pb.PropertyType));
                Program.WriteLog($"VarFunctionByte={(isArray ? 1 : 0)} set on {t.Name}");
                return;
            }
            var pf = t.GetProperty("VarFunction", AnyInstance);
            if (pf != null && pf.CanWrite && pf.PropertyType.IsEnum)
            {
                var name = isArray ? "VArray" : Enum.GetNames(pf.PropertyType)[0];
                pf.SetValue(itVar, Enum.Parse(pf.PropertyType, name));
                Program.WriteLog($"VarFunction={name} set on {t.Name}");
                return;
            }
            var p = t.GetProperty("IsArray", AnyInstance);
            if (p != null && p.CanWrite) { p.SetValue(itVar, isArray); Program.WriteLog($"IsArray={isArray} set on {t.Name}"); return; }
            Program.WriteLog("WARNING: no writable VarFunctionByte/VarFunction/IsArray on " + t.FullName);
        }

        public static RpcResponse UpdateDbVariable(HostBootstrap host, RpcRequest req)
        {
            try
            {
                var listName = req.GetStringParam("database");
                var varName = req.GetStringParam("variable");
                if (string.IsNullOrEmpty(listName)) throw new ArgumentException("'database' is required");
                if (string.IsNullOrEmpty(varName)) throw new ArgumentException("'variable' is required");

                var (itVar, _, _) = ResolveVariable(listName, varName);
                var varT = itVar.GetType();

                bool changed = false;
                if (TryParam(req, "default", out var defaultVal))
                {
                    // Same TRUE/FALSE → 1/0 mapping the create path applies.
                    var existingType = varT.GetProperty("VarType")?.GetValue(itVar)?.ToString() ?? "";
                    defaultVal = NormalizeDefault(existingType, defaultVal);
                    SetIsArray(itVar, SplitDefault(defaultVal).Length > 1);
                    var values = (IEnumerable)varT.GetProperty("Values")!.GetValue(itVar)!;
                    foreach (var v in values) SetDefaultOnTValue(v, defaultVal);
                    changed = true;
                }
                if (TryParam(req, "min", out var min))
                {
                    varT.GetProperty("Min")?.SetValue(itVar, min);
                    changed = true;
                }
                if (TryParam(req, "max", out var max))
                {
                    varT.GetProperty("Max")?.SetValue(itVar, max);
                    changed = true;
                }
                if (TryParam(req, "unit", out var unit))
                {
                    varT.GetProperty("UnitEcu")?.SetValue(itVar, unit);
                    changed = true;
                }
                if (TryParam(req, "description", out var description))
                {
                    varT.GetProperty("Description")?.SetValue(itVar, description);
                    changed = true;
                }

                if (!changed)
                    throw new ArgumentException("No changes specified. Provide at least one of: default, min, max, unit, description.");

                var varId = (Guid)varT.GetProperty("GUID")!.GetValue(itVar)!;
                Program.WriteLog($"update_db_variable: '{varName}' (id={varId}) updated");

                host.SaveProject();

                return RpcResponse.Ok(req.Id, new
                {
                    status = "ok",
                    message = $"Updated variable '{varName}' in '{listName}'",
                    name = varName,
                    var_id = varId.ToString(),
                });
            }
            catch (Exception ex)
            {
                Program.WriteLog($"update_db_variable failed: {ex}");
                return RpcResponse.Fail(req.Id, RpcErrorCodes.InternalError,
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        public static RpcResponse DeleteDbVariable(HostBootstrap host, RpcRequest req)
        {
            try
            {
                var listName = req.GetStringParam("database");
                var varName = req.GetStringParam("variable");
                if (string.IsNullOrEmpty(listName)) throw new ArgumentException("'database' is required");
                if (string.IsNullOrEmpty(varName)) throw new ArgumentException("'variable' is required");

                var (itVar, _, oldFactoryType) = ResolveVariable(listName, varName);
                var oldFactory = HostBootstrap.ResolveByReflection(oldFactoryType)
                    ?? throw new InvalidOperationException("IOldDatabaseVariableFactory not registered");

                var varId = (Guid)itVar.GetType().GetProperty("GUID")!.GetValue(itVar)!;

                // Release(ITVar) handles cascade: HstGroups, BlockParameter links, Values,
                // repository delete. The contract surface for delete that we can rely on.
                var release = oldFactoryType.GetMethod("Release")!;
                try { release.Invoke(oldFactory, new[] { itVar }); }
                catch (TargetInvocationException tie) when (tie.InnerException is not null) { throw tie.InnerException; }

                Program.WriteLog($"delete_db_variable: '{varName}' (id={varId}) released");

                host.SaveProject();

                return RpcResponse.Ok(req.Id, new
                {
                    status = "ok",
                    message = $"Deleted variable '{varName}' from '{listName}'",
                    name = varName,
                    var_id = varId.ToString(),
                });
            }
            catch (Exception ex)
            {
                Program.WriteLog($"delete_db_variable failed: {ex}");
                return RpcResponse.Fail(req.Id, RpcErrorCodes.InternalError,
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Resolve an ITVar by (listName, varName) on the first VirtualEcu. Returns the
        /// ITVar plus the IOldDatabaseVariableFactory type for callers that need it
        /// (delete) so we don't duplicate the assembly+type lookups.
        /// </summary>
        private static (object itVar, object varRepo, Type oldFactoryType) ResolveVariable(string listName, string varName)
        {
            var dbContracts = Assembly.Load("Hydac.PDT.Database.Business.Contracts");
            var businessIfaces = Assembly.Load("Hydac.PDT.Business.Interfaces");
            var dbObjects = Assembly.Load("DatabaseObjects");

            var listRepoType = dbContracts.GetType(
                "Hydac.PDT.Database.Business.Contracts.IDatabaseListRepository", throwOnError: true)!;
            var varRepoType = dbContracts.GetType(
                "Hydac.PDT.Database.Business.Contracts.Variables.IDatabaseVariableRepository", throwOnError: true)!;
            var oldFactoryType = dbObjects.GetType("Hydac.PDT.ViewModel.IOldDatabaseVariableFactory", throwOnError: false)
                ?? FindTypeBySimpleName("IOldDatabaseVariableFactory")
                ?? throw new InvalidOperationException("IOldDatabaseVariableFactory type not found");

            var listRepo = HostBootstrap.ResolveByReflection(listRepoType)
                ?? throw new InvalidOperationException("IDatabaseListRepository not registered");
            var varRepo = HostBootstrap.ResolveByReflection(varRepoType)
                ?? throw new InvalidOperationException("IDatabaseVariableRepository not registered");

            var projectServiceType = businessIfaces.GetType(
                "Hydac.PDT.Business.Contracts.Project.IProjectService", throwOnError: true)!;
            var projectService = HostBootstrap.ResolveByReflection(projectServiceType)
                ?? throw new InvalidOperationException("IProjectService not registered");
            var actualAgent = projectServiceType.GetProperty("ActualProjectAgent")!.GetValue(projectService)
                ?? throw new InvalidOperationException("No project loaded");
            var virtualEcus = (IEnumerable)actualAgent.GetType().GetProperty("VirtualEcus")!.GetValue(actualAgent)!;
            var firstEcu = virtualEcus.Cast<object>().FirstOrDefault()
                ?? throw new InvalidOperationException("Project has no VirtualEcus");
            var ecuId = (Guid)firstEcu.GetType().GetProperty("ObjectId")!.GetValue(firstEcu)!;

            var dbList = listRepoType.GetMethod("GetByEcuIdAndName")!.Invoke(listRepo, new object[] { ecuId, listName })
                ?? throw new ArgumentException($"Database list '{listName}' not found.");
            var listId = (Guid)dbList.GetType().GetProperty("Id")!.GetValue(dbList)!;

            // ITVar lives in TRepository.Variables, but we need an interface-typed handle
            // to call Release(ITVar). The repository contract returns DatabaseVariable
            // (model), not ITVar — so we walk projectAgent.Repository.Variables directly.
            var repository = actualAgent.GetType().GetProperty("Repository")!.GetValue(actualAgent)!;
            var allVars = (IEnumerable)repository.GetType().GetProperty("Variables")!.GetValue(repository)!;
            object? itVar = null;
            foreach (var v in allVars)
            {
                var vType = v.GetType();
                var vListId = (Guid)vType.GetProperty("DatabaseListId")!.GetValue(v)!;
                var vName = (string?)vType.GetProperty("Name")?.GetValue(v);
                if (vListId == listId && string.Equals(vName, varName, StringComparison.OrdinalIgnoreCase))
                {
                    itVar = v;
                    break;
                }
            }
            if (itVar == null)
                throw new ArgumentException($"Variable '{varName}' not found in '{listName}'.");

            return (itVar, varRepo, oldFactoryType);
        }

        private static bool TryParam(RpcRequest req, string name, out string value)
        {
            value = req.GetStringParam(name, "");
            // Treat empty string as "not provided" — matches v1 contract where empty
            // strings mean "keep current".
            return !string.IsNullOrEmpty(value);
        }

        private static Type? FindTypeBySimpleName(string name)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetExportedTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.Name == name);
        }
    }
}
