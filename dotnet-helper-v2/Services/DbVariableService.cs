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
                Program.WriteLog($"add_db_variable: created '{name}' (id={varId}) in list '{listName}' (id={listId}) as {varType}");

                host.SaveProject();

                return RpcResponse.Ok(req.Id, new
                {
                    status = "ok",
                    message = $"Created variable '{name}' in '{listName}' as {varType}",
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

            defaults.Clear();
            foreach (var ch in defaultStr)
            {
                var entry = createOne.Invoke(factory, new object[] { ch.ToString() });
                if (entry != null) defaults.Add(entry);
            }
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
