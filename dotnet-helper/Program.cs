using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text.Json;
using System.Xml.Linq;

#pragma warning disable SYSLIB0011 // BinaryFormatter is obsolete
#pragma warning disable CS8632     // Nullable annotations outside #nullable context

/// <summary>
/// Deserializes .dat files from a HYDAC PDT .hdb archive using BinaryFormatter
/// and the PDT .NET assemblies. Outputs JSON to stdout.
///
/// Usage: HdbDatReader &lt;hdb_path&gt; &lt;pdt_dir&gt; &lt;command&gt; [args]
///   commands: errors, compileconfig, isobus, dump &lt;file&gt;, dump-all, list-dat,
///            db-list-vars [database], db-get-var &lt;database&gt; &lt;variable&gt;,
///            db-add-var, db-update-var, db-delete-var
/// </summary>
class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: HdbDatReader <hdb_path> <pdt_dir> <command> [args]");
            Console.Error.WriteLine("  commands: errors, compileconfig, isobus, dump <file>, dump-all, list-dat,");
            Console.Error.WriteLine("           db-list-vars [database], db-get-var <database> <variable>,");
            Console.Error.WriteLine("           db-add-var, db-update-var, db-delete-var");
            return 1;
        }

        string hdbPath = args[0];
        string pdtDir = args[1];
        string command = args[2].ToLowerInvariant();

        if (!File.Exists(hdbPath))
        {
            Console.Error.WriteLine($"HDB file not found: {hdbPath}");
            return 1;
        }

        if (!Directory.Exists(pdtDir))
        {
            Console.Error.WriteLine($"PDT directory not found: {pdtDir}");
            return 1;
        }

        // Resolve PDT assemblies dynamically
        AppDomain.CurrentDomain.AssemblyResolve += (sender, resolveArgs) =>
        {
            string name = new AssemblyName(resolveArgs.Name).Name;
            string path = Path.Combine(pdtDir, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };

        try
        {
            // Write commands need the hdbPath for re-writing; read commands use ZipFile.OpenRead
            if (command == "db-add-var" || command == "db-update-var" || command == "db-delete-var"
                || command == "err-custom-add")
            {
                // Read JSON payload from stdin
                string stdinJson = Console.In.ReadToEnd();
                string json = command switch
                {
                    "db-add-var" => DbAddVar(hdbPath, stdinJson),
                    "db-update-var" => DbUpdateVar(hdbPath, stdinJson),
                    "db-delete-var" => DbDeleteVar(hdbPath, stdinJson),
                    "err-custom-add" => CustomErrorAdd(hdbPath, stdinJson),
                    _ => throw new ArgumentException($"Unknown command: {command}")
                };
                Console.Write(json);
                return 0;
            }

            using var zip = ZipFile.OpenRead(hdbPath);
            string result = command switch
            {
                "errors" => ReadErrors(zip),
                "compileconfig" => ReadCompileConfig(zip),
                "isobus" => ReadIsobus(zip),
                "list-dat" => ListDatFiles(zip),
                "dump" => DumpDatFile(zip, args.Length > 3 ? args[3] : throw new ArgumentException("dump requires a filename argument")),
                "dump-all" => DumpAllDatFiles(zip),
                "db-list-vars" => DbListVars(zip, args.Length > 3 ? args[3] : ""),
                "db-get-var" => DbGetVar(zip, args.Length > 3 ? args[3] : throw new ArgumentException("db-get-var requires database name"),
                                              args.Length > 4 ? args[4] : throw new ArgumentException("db-get-var requires variable name")),
                "err-list-dms" => ReadDetectionMethods(zip, args.Length > 3 ? args[3] : ""),
                "err-list-fmis" => ReadFmiDefinitions(zip),
                "err-list-templates" => ReadErrorTemplates(zip),
                _ => throw new ArgumentException($"Unknown command: {command}")
            };
            Console.Write(result);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (ex.InnerException != null)
                Console.Error.WriteLine($"  Inner: {ex.InnerException.Message}");
            return 1;
        }
    }

    static object Deserialize(ZipArchive zip, string datFile)
    {
        var entry = zip.GetEntry(datFile)
            ?? throw new FileNotFoundException($"{datFile} not found in archive");

        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;

        var formatter = new BinaryFormatter();
        return formatter.Deserialize(ms);
    }

    // -----------------------------------------------------------------------
    // Curated commands (existing)
    // -----------------------------------------------------------------------

    static string ReadErrors(ZipArchive zip)
    {
        var obj = Deserialize(zip, "Errors.dat");
        var errorsProp = obj.GetType().GetProperty("Errors");
        var errors = errorsProp?.GetValue(obj) as IList;

        if (errors == null || errors.Count == 0)
            return "[]";

        var result = new List<object>();
        foreach (var error in errors)
        {
            var setProps = GetProp(error, "SetProperties");
            var releaseProps = GetProp(error, "ReleaseProperties");
            var reactionProps = GetProp(error, "ReactionProperties");

            result.Add(new
            {
                spn = GetPropValue<int>(error, "Spn"),
                description = GetPropValue<string>(error, "Description") ?? "",
                severity = GetPropValue<int>(error, "Severity"),
                error_type = GetPropValue<int>(error, "ErrorType"),
                store_behaviour = GetPropValue<int>(error, "ErrorStoreBehaviour"),
                comment = GetPropValue<string>(error, "Comment") ?? "",
                symbol = GetPropValue<string>(error, "Symbol") ?? "",
                error_info_page = GetPropValue<int>(error, "ErrorInformationPageIndex"),
                object_id = GetPropValue<string>(error, "ObjectId") ?? "",
                owner_id = GetPropValue<string>(error, "OwnerId") ?? "",
                detection_method = GetPropValue<string>(error, "DetectionMethod") ?? "",
                fmi = GetPropValue<string>(error, "Fmi") ?? "",
                fmi_extended = GetPropValue<string>(error, "FmiExtended") ?? "",
                restricted_mode = reactionProps != null ? (GetPropValue<string>(reactionProps, "RestrictedMode") ?? "") : "",
                set_debounce_enabled = setProps != null && GetPropValue<bool>(setProps, "IsDebounceEnabled"),
                set_debounce_ms = setProps != null ? GetPropValue<int>(setProps, "DebounceTime") : 0,
                set_threshold = setProps != null ? GetPropValue<int>(setProps, "Threshold") : 0,
                release_debounce_enabled = releaseProps != null && GetPropValue<bool>(releaseProps, "IsDebounceEnabled"),
                release_debounce_ms = releaseProps != null ? GetPropValue<int>(releaseProps, "DebounceTime") : 0,
                release_threshold = releaseProps != null ? GetPropValue<int>(releaseProps, "Threshold") : 0,
                reaction_advanced_info = reactionProps != null ? GetPropValue<int>(reactionProps, "AdvancedErrorInformation") : 0,
            });
        }

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    static string ReadDetectionMethods(ZipArchive zip, string filter)
    {
        var dataLayer = Deserialize(zip, "project.dat");
        var repo = GetProp(dataLayer, "Repo");
        if (repo == null) return "[]";

        var result = new List<object>();

        // Read custom detection method templates from DetectionMethodData
        var repoProject = GetProp(repo, "RepoProject");
        var detail = repoProject != null ? GetProp(repoProject, "Detail") : null;
        var dmData = detail != null ? GetProp(detail, "DetectionMethodData") : null;

        if (dmData != null)
        {
            foreach (var setName in new[] { "Custom", "Default" })
            {
                var templateSet = GetProp(dmData, setName);
                if (templateSet == null) continue;
                var dms = GetProp(templateSet, "DetectionMethods") as IList;
                if (dms == null) continue;

                foreach (var dm in dms)
                {
                    if (dm == null || dm is string) continue;
                    var detection = GetPropValue<string>(dm, "Detection") ?? "";
                    if (!string.IsNullOrEmpty(filter) &&
                        detection.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    result.Add(new
                    {
                        detection = detection,
                        detection_vm = GetPropValue<string>(dm, "DetectionVm") ?? "",
                        detection_method_name = GetPropValue<string>(dm, "DetectionMethodName") ?? "",
                        bit = GetPropValue<int>(dm, "Bit"),
                        default_fmi = GetPropValue<int>(dm, "DefaultFmi"),
                        default_fmi_ex = GetPropValue<int>(dm, "DefaultFmiEx"),
                        set_condition = GetPropValue<string>(dm, "SetCondition") ?? "",
                        release_condition = GetPropValue<string>(dm, "ReleaseCondition") ?? "",
                        group_name = GetPropValue<string>(dm, "GroupName") ?? "",
                        description = GetPropValue<string>(dm, "Description") ?? "",
                        source = setName,
                    });
                }
            }
        }

        // Read TDetectionMethod objects from Repo.DetectionMethod for GUID mapping
        var dmList = GetProp(repo, "DetectionMethod") as IList;
        if (dmList != null)
        {
            foreach (var dm in dmList)
            {
                if (dm == null || dm is string) continue;
                var name = GetPropValue<string>(dm, "Name") ?? "";
                var guid = GetPropValue<string>(dm, "ObjectId") ?? GetPropValue<string>(dm, "GUID") ?? "";
                if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(guid)) continue;
                if (!string.IsNullOrEmpty(filter) &&
                    name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                result.Add(new
                {
                    detection = name,
                    detection_vm = "",
                    detection_method_name = "",
                    bit = -1,
                    default_fmi = -1,
                    default_fmi_ex = -1,
                    set_condition = "",
                    release_condition = "",
                    group_name = "",
                    description = "",
                    source = "Repo",
                    guid = guid,
                });
            }
        }

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    static string ReadFmiDefinitions(ZipArchive zip)
    {
        var dataLayer = Deserialize(zip, "project.dat");
        var repo = GetProp(dataLayer, "Repo");
        if (repo == null) return "{}";

        var result = new { fmis = new List<object>(), fmi_exts = new List<object>() };

        var fmis = GetProp(repo, "Fmis") as IList;
        if (fmis != null)
        {
            foreach (var fmi in fmis)
            {
                if (fmi == null || fmi is string) continue;
                result.fmis.Add(new
                {
                    name = GetPropValue<string>(fmi, "Name") ?? "",
                    value = GetPropValue<int>(fmi, "Value"),
                    guid = GetPropValue<string>(fmi, "ObjectId") ?? GetPropValue<string>(fmi, "GUID") ?? "",
                    description = GetPropValue<string>(fmi, "Description") ?? "",
                });
            }
        }

        var fmiExts = GetProp(repo, "FmiExts") as IList;
        if (fmiExts != null)
        {
            foreach (var fmiEx in fmiExts)
            {
                if (fmiEx == null || fmiEx is string) continue;
                result.fmi_exts.Add(new
                {
                    name = GetPropValue<string>(fmiEx, "Name") ?? "",
                    value = GetPropValue<int>(fmiEx, "Value"),
                    guid = GetPropValue<string>(fmiEx, "ObjectId") ?? GetPropValue<string>(fmiEx, "GUID") ?? "",
                    description = GetPropValue<string>(fmiEx, "Description") ?? "",
                });
            }
        }

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    static string ReadErrorTemplates(ZipArchive zip)
    {
        var dataLayer = Deserialize(zip, "project.dat");
        var repo = GetProp(dataLayer, "Repo");
        if (repo == null) return "[]";

        var result = new List<object>();

        // ErrorTemplates are at DetectionMethodData.Custom.ErrorTemplates
        // and DetectionMethodData.Default.ErrorTemplates
        var repoProject = GetProp(repo, "RepoProject");
        var detail = repoProject != null ? GetProp(repoProject, "Detail") : null;
        var dmData = detail != null ? GetProp(detail, "DetectionMethodData") : null;

        if (dmData != null)
        {
            foreach (var setName in new[] { "Custom", "Default" })
            {
                var templateSet = GetProp(dmData, setName);
                if (templateSet == null) continue;
                var templates = GetProp(templateSet, "ErrorTemplates") as IList;
                if (templates == null || templates.Count == 0) continue;

                foreach (var tmpl in templates)
                {
                    if (tmpl == null || tmpl is string) continue;
                    result.Add(new
                    {
                        type = GetPropValue<string>(tmpl, "Type") ?? "",
                        name = GetPropValue<string>(tmpl, "Name") ?? "",
                        guid = GetPropValue<string>(tmpl, "ObjectId") ?? GetPropValue<string>(tmpl, "GUID") ?? "",
                        description = GetPropValue<string>(tmpl, "Description") ?? "",
                        source = setName,
                    });
                }
            }
        }

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    static string ReadCompileConfig(ZipArchive zip)
    {
        var obj = Deserialize(zip, "CompileConfig.dat");
        var result = new
        {
            compile_mode = GetPropValue<object>(obj, "CompileMode")?.ToString() ?? "",
            build_configuration = GetPropValue<int>(obj, "BuildConfiguration"),
            service_file_type = GetPropValue<object>(obj, "ServiceFileType")?.ToString() ?? "",
            is_software_module_based = GetPropValue<bool>(obj, "IsSoftwareModuleBased"),
            is_timestamp_in_autocode = GetPropValue<bool>(obj, "IsOutputTimeStampIntoAutoCode"),
            log_level = GetPropValue<int>(obj, "LogLevel"),
        };
        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    static string ReadIsobus(ZipArchive zip)
    {
        var obj = Deserialize(zip, "Isobus.dat");
        var result = new
        {
            id = GetPropValue<Guid>(obj, "ID").ToString(),
            shutdown_storage = GetPropValue<object>(obj, "ShutDownStorageBehavior")?.ToString() ?? "",
        };
        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    // -----------------------------------------------------------------------
    // Generic dump commands (new)
    // -----------------------------------------------------------------------

    static string ListDatFiles(ZipArchive zip)
    {
        var files = new List<object>();
        foreach (var entry in zip.Entries)
        {
            if (entry.Name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
            {
                files.Add(new
                {
                    name = entry.FullName,
                    size = entry.Length,
                    compressed = entry.CompressedLength
                });
            }
        }
        return JsonSerializer.Serialize(files, new JsonSerializerOptions { WriteIndented = true });
    }

    static string DumpDatFile(ZipArchive zip, string filename)
    {
        var obj = Deserialize(zip, filename);
        var dumped = ObjectDumper.Dump(obj);
        return JsonSerializer.Serialize(dumped, new JsonSerializerOptions { WriteIndented = true });
    }

    static string DumpAllDatFiles(ZipArchive zip)
    {
        var result = new Dictionary<string, object>();
        foreach (var entry in zip.Entries)
        {
            if (!entry.Name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var obj = Deserialize(zip, entry.FullName);
                result[entry.FullName] = ObjectDumper.Dump(obj);
            }
            catch (Exception ex)
            {
                result[entry.FullName] = new Dictionary<string, string>
                {
                    { "$error", ex.Message }
                };
            }
        }
        // Compact JSON for dump-all (can be large)
        return JsonSerializer.Serialize(result);
    }

    // -----------------------------------------------------------------------
    // DB variable helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Build a GUID-to-database-name map from DatabaseLists.xml in the ZIP.
    /// </summary>
    static Dictionary<string, string> BuildDbNameMap(ZipArchive zip)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entry = zip.GetEntry("DatabaseLists.xml");
        if (entry == null) return map;

        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var xml = System.Text.Encoding.UTF8.GetString(ms.ToArray().TakeWhile(b => b != 0).ToArray());
        var doc = XDocument.Parse(xml);

        foreach (var dbEl in doc.Root.Elements())
        {
            var id = dbEl.Element("Id")?.Value ?? "";
            var name = dbEl.Element("Name")?.Value ?? "";
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                map[id] = name;
        }
        return map;
    }

    /// <summary>
    /// Get the Variables collection from project.dat and the database name map.
    /// </summary>
    static (IList variables, Dictionary<string, string> dbNameMap) GetVariablesAndDbMap(ZipArchive zip)
    {
        var dataLayer = Deserialize(zip, "project.dat");
        var repo = GetProp(dataLayer, "Repo")
            ?? throw new InvalidOperationException("project.dat: Repo property not found");
        var variables = GetProp(repo, "Variables") as IList
            ?? throw new InvalidOperationException("project.dat: Repo.Variables not found");
        var dbNameMap = BuildDbNameMap(zip);
        return (variables, dbNameMap);
    }

    /// <summary>
    /// Extract a compact JSON representation of a TVar for list output.
    /// </summary>
    static object VarToCompact(object tvar, Dictionary<string, string> dbNameMap)
    {
        var dbListId = GetPropValue<string>(tvar, "DatabaseListId") ?? "";
        dbNameMap.TryGetValue(dbListId, out var dbName);

        // Get default value from Values[0].DisplayValue
        string defaultVal = "";
        try
        {
            var values = GetProp(tvar, "Values") as IList;
            if (values != null && values.Count > 0)
                defaultVal = GetPropValue<string>(values[0], "DisplayValue") ?? "";
        }
        catch
        {
            // Values getter may fail if tLoadedTValues is null (corrupted clone).
            // Fall back to Detail.Defaults.
            try
            {
                var detail = GetProp(tvar, "Detail");
                if (detail != null)
                {
                    var defaults = GetProp(detail, "Defaults") as IList;
                    if (defaults != null && defaults.Count > 0)
                        defaultVal = GetPropValue<string>(defaults[0], "Value") ?? "";
                }
            }
            catch { }
        }

        // Get type prefix from TType
        string typePrefix = "";
        var ttype = GetProp(tvar, "TType");
        if (ttype != null)
            typePrefix = GetPropValue<string>(ttype, "Prefix") ?? "";

        return new
        {
            name = GetPropValue<string>(tvar, "Name") ?? "",
            database = dbName ?? dbListId,
            database_id = dbListId,
            var_type = GetPropValue<string>(tvar, "VarType") ?? "",
            type_prefix = typePrefix,
            default_value = defaultVal,
            min = GetPropValue<string>(tvar, "Min") ?? "",
            max = GetPropValue<string>(tvar, "Max") ?? "",
            unit = GetPropValue<string>(tvar, "UnitEcu") ?? "",
            description = GetPropValue<string>(tvar, "Description") ?? "",
            comm_id = GetPropValue<int>(tvar, "CommID"),
            idx = GetPropValue<int>(tvar, "Idx"),
            guid = GetPropValue<string>(tvar, "GUID") ?? "",
        };
    }

    /// <summary>
    /// Extract a detailed JSON representation of a TVar.
    /// </summary>
    static object VarToDetailed(object tvar, Dictionary<string, string> dbNameMap)
    {
        var dbListId = GetPropValue<string>(tvar, "DatabaseListId") ?? "";
        dbNameMap.TryGetValue(dbListId, out var dbName);

        // Default value and access levels from Values[0]
        string defaultVal = "";
        var accessLevels = new Dictionary<string, string>();
        var datasetValues = new List<object>();
        try
        {
            var values = GetProp(tvar, "Values") as IList;
            if (values != null && values.Count > 0)
            {
                var val0 = values[0];
                defaultVal = GetPropValue<string>(val0, "DisplayValue") ?? "";

                // Access levels from Detail.AccessLevel
                var valDetail = GetProp(val0, "Detail");
                if (valDetail != null)
                {
                    var al = GetProp(valDetail, "AccessLevel") as IDictionary;
                    if (al != null)
                    {
                        foreach (DictionaryEntry e in al)
                            accessLevels[e.Key.ToString()] = e.Value?.ToString() ?? "";
                    }
                }

                // Dataset array values
                var dsValues = GetProp(val0, "DatasetArrayValues") as IList;
                if (dsValues != null)
                {
                    foreach (var dsv in dsValues)
                    {
                        datasetValues.Add(new
                        {
                            value = GetPropValue<string>(dsv, "Value") ?? "",
                            index = GetPropValue<int>(dsv, "Index"),
                            description = GetPropValue<string>(dsv, "Description") ?? "",
                            minimum = GetPropValue<string>(dsv, "Minimum") ?? "",
                            maximum = GetPropValue<string>(dsv, "Maximum") ?? "",
                        });
                    }
                }
            }
        }
        catch
        {
            // Values getter may fail on corrupted clones — fall back to Detail
            try
            {
                var detail = GetProp(tvar, "Detail");
                if (detail != null)
                {
                    var defaults = GetProp(detail, "Defaults") as IList;
                    if (defaults != null && defaults.Count > 0)
                        defaultVal = GetPropValue<string>(defaults[0], "Value") ?? "";
                }
            }
            catch { }
        }

        // Type info
        string typePrefix = "", typeName = "", typeGuid = "";
        var ttype = GetProp(tvar, "TType");
        if (ttype != null)
        {
            typePrefix = GetPropValue<string>(ttype, "Prefix") ?? "";
            typeName = GetPropValue<string>(ttype, "Name") ?? "";
            typeGuid = GetPropValue<string>(ttype, "GUID") ?? "";
        }

        return new
        {
            name = GetPropValue<string>(tvar, "Name") ?? "",
            database = dbName ?? dbListId,
            database_id = dbListId,
            var_type = GetPropValue<string>(tvar, "VarType") ?? "",
            var_type_byte = GetPropValue<int>(tvar, "VarTypeByte"),
            type_prefix = typePrefix,
            type_name = typeName,
            type_guid = typeGuid,
            var_function = GetPropValue<string>(tvar, "VarFunction") ?? "",
            default_value = defaultVal,
            min = GetPropValue<string>(tvar, "Min") ?? "",
            max = GetPropValue<string>(tvar, "Max") ?? "",
            unit = GetPropValue<string>(tvar, "UnitEcu") ?? "",
            description = GetPropValue<string>(tvar, "Description") ?? "",
            notes = GetPropValue<string>(tvar, "Notes") ?? "",
            comm_id = GetPropValue<int>(tvar, "CommID"),
            idx = GetPropValue<int>(tvar, "Idx"),
            guid = GetPropValue<string>(tvar, "GUID") ?? "",
            nv_mem_address = GetPropValue<int>(tvar, "NvMemAddress"),
            hst_scaling_offset = GetPropValue<int>(tvar, "HstScalingOffset"),
            hst_scaling_factor = GetPropValue<int>(tvar, "HstScalingFactor"),
            hst_scaling_unit = GetPropValue<string>(tvar, "HstScalingUnit") ?? "",
            access_levels = accessLevels,
            dataset_values = datasetValues,
        };
    }

    // -----------------------------------------------------------------------
    // DB variable read commands
    // -----------------------------------------------------------------------

    static string DbListVars(ZipArchive zip, string databaseFilter)
    {
        var (variables, dbNameMap) = GetVariablesAndDbMap(zip);

        // Resolve database filter to GUID if it's a name
        string filterDbId = "";
        if (!string.IsNullOrEmpty(databaseFilter))
        {
            // Try as GUID first
            if (dbNameMap.ContainsKey(databaseFilter))
            {
                filterDbId = databaseFilter;
            }
            else
            {
                // Find by name (case-insensitive)
                foreach (var kvp in dbNameMap)
                {
                    if (kvp.Value.Equals(databaseFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        filterDbId = kvp.Key;
                        break;
                    }
                }
                if (string.IsNullOrEmpty(filterDbId))
                    throw new ArgumentException($"Database '{databaseFilter}' not found. Use list_databases to see available databases.");
            }
        }

        var result = new List<object>();
        foreach (var tvar in variables)
        {
            if (tvar == null || tvar is string) continue;

            if (!string.IsNullOrEmpty(filterDbId))
            {
                var varDbId = GetPropValue<string>(tvar, "DatabaseListId") ?? "";
                if (!varDbId.Equals(filterDbId, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            result.Add(VarToCompact(tvar, dbNameMap));
        }

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    static string DbGetVar(ZipArchive zip, string databaseName, string variableName)
    {
        var (variables, dbNameMap) = GetVariablesAndDbMap(zip);

        // Resolve database name to GUID
        string targetDbId = "";
        foreach (var kvp in dbNameMap)
        {
            if (kvp.Value.Equals(databaseName, StringComparison.OrdinalIgnoreCase))
            {
                targetDbId = kvp.Key;
                break;
            }
        }
        if (string.IsNullOrEmpty(targetDbId))
            throw new ArgumentException($"Database '{databaseName}' not found.");

        // Find the variable
        foreach (var tvar in variables)
        {
            if (tvar == null || tvar is string) continue;
            var varDbId = GetPropValue<string>(tvar, "DatabaseListId") ?? "";
            if (!varDbId.Equals(targetDbId, StringComparison.OrdinalIgnoreCase))
                continue;
            var varName = GetPropValue<string>(tvar, "Name") ?? "";
            if (varName.Equals(variableName, StringComparison.OrdinalIgnoreCase))
                return JsonSerializer.Serialize(VarToDetailed(tvar, dbNameMap), new JsonSerializerOptions { WriteIndented = true });
        }

        throw new ArgumentException($"Variable '{variableName}' not found in database '{databaseName}'.");
    }

    // -----------------------------------------------------------------------
    // DB variable write commands
    // -----------------------------------------------------------------------

    /// <summary>
    /// Deserialize project.dat, apply a modification, serialize back, and write to HDB.
    /// Creates .hdb.bak backup before first write.
    /// </summary>
    static void ModifyProjectDat(string hdbPath, Action<object, IList, Dictionary<string, string>, byte[]> modifier)
    {
        // Read all ZIP entries into memory
        var entries = new Dictionary<string, byte[]>();
        Dictionary<string, string> dbNameMap;

        using (var zip = ZipFile.OpenRead(hdbPath))
        {
            dbNameMap = BuildDbNameMap(zip);

            foreach (var entry in zip.Entries)
            {
                using var ms = new MemoryStream();
                using var s = entry.Open();
                s.CopyTo(ms);
                entries[entry.FullName] = ms.ToArray();
            }
        }

        // Deserialize project.dat
        object dataLayer;
        using (var ms = new MemoryStream(entries["project.dat"]))
        {
            var formatter = new BinaryFormatter();
            dataLayer = formatter.Deserialize(ms);
        }

        var repo = GetProp(dataLayer, "Repo")
            ?? throw new InvalidOperationException("project.dat: Repo not found");
        var variables = GetProp(repo, "Variables") as IList
            ?? throw new InvalidOperationException("project.dat: Variables not found");

        // Apply modification — pass raw project.dat bytes for clone operations
        modifier(repo, variables, dbNameMap, entries["project.dat"]);

        // Force-initialize tTValues on ALL variables before serialization.
        // After deserialization, tLoadedTValues is populated by [OnDeserialized]
        // but tTValues stays null (lazy). BinaryFormatter only serializes tTValues
        // (tLoadedTValues is [NonSerialized]). Accessing the Values getter forces
        // tTValues construction so it's non-null when serialized.
        foreach (var v in variables)
        {
            if (v == null || v is string) continue;
            try { var _ = GetProp(v, "Values"); } catch { }
        }

        // Serialize back
        byte[] newProjectDat;
        using (var ms = new MemoryStream())
        {
            var formatter = new BinaryFormatter();
            formatter.Serialize(ms, dataLayer);
            newProjectDat = ms.ToArray();
        }
        entries["project.dat"] = newProjectDat;

        // Create backup
        string backupPath = hdbPath + ".bak";
        if (!File.Exists(backupPath))
            File.Copy(hdbPath, backupPath);

        // Write new HDB to temp file, then replace
        string tmpPath = hdbPath + ".tmp";
        try
        {
            using (var zipOut = ZipFile.Open(tmpPath, ZipArchiveMode.Create))
            {
                foreach (var kvp in entries)
                {
                    var entry = zipOut.CreateEntry(kvp.Key, CompressionLevel.Optimal);
                    using var s = entry.Open();
                    s.Write(kvp.Value, 0, kvp.Value.Length);
                }
            }
            File.Delete(hdbPath);
            File.Move(tmpPath, hdbPath);
        }
        catch
        {
            if (File.Exists(tmpPath))
                File.Delete(tmpPath);
            throw;
        }
    }

    /// <summary>
    /// Resolve a database name to its GUID. Throws if not found.
    /// </summary>
    static string ResolveDbId(Dictionary<string, string> dbNameMap, string databaseName)
    {
        foreach (var kvp in dbNameMap)
        {
            if (kvp.Value.Equals(databaseName, StringComparison.OrdinalIgnoreCase))
                return kvp.Key;
        }
        throw new ArgumentException($"Database '{databaseName}' not found. Use list_databases to see available databases.");
    }

    /// <summary>
    /// Find a TVar by database ID and variable name. Throws if not found.
    /// </summary>
    static object FindVar(IList variables, string dbId, string varName)
    {
        foreach (var tvar in variables)
        {
            if (tvar == null || tvar is string) continue;
            var varDbId = GetPropValue<string>(tvar, "DatabaseListId") ?? "";
            if (!varDbId.Equals(dbId, StringComparison.OrdinalIgnoreCase)) continue;
            var name = GetPropValue<string>(tvar, "Name") ?? "";
            if (name.Equals(varName, StringComparison.OrdinalIgnoreCase))
                return tvar;
        }
        throw new ArgumentException($"Variable '{varName}' not found in database.");
    }

    /// <summary>
    /// Get a value from a dictionary with a default fallback (.NET 4.8 compat).
    /// </summary>
    static string DictGet(Dictionary<string, string> dict, string key, string defaultValue = "")
    {
        return dict.TryGetValue(key, out var val) ? val : defaultValue;
    }

    /// <summary>
    /// Set a property value on an object via reflection.
    /// </summary>
    static void SetProp(object obj, string name, object value)
    {
        var prop = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (prop == null || !prop.CanWrite) return;
        try
        {
            var target = value;
            if (prop.PropertyType == typeof(Guid) && value is string guidStr)
                target = Guid.Parse(guidStr);
            else if (value != null && !prop.PropertyType.IsAssignableFrom(value.GetType()))
                target = Convert.ChangeType(value, prop.PropertyType);
            prop.SetValue(obj, target);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"SetProp failed on '{name}' (type {prop.PropertyType.Name}): {ex.InnerException?.Message ?? ex.Message}", ex);
        }
    }

    static string DbAddVar(string hdbPath, string stdinJson)
    {
        var payload = JsonSerializer.Deserialize<Dictionary<string, string>>(stdinJson)
            ?? throw new ArgumentException("Invalid JSON input");

        string database = DictGet(payload, "database");
        if (string.IsNullOrEmpty(database)) throw new ArgumentException("'database' is required");
        string name = DictGet(payload, "name");
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("'name' is required");
        string varType = DictGet(payload, "type");
        if (string.IsNullOrEmpty(varType)) throw new ArgumentException("'type' is required");
        string defaultValue = DictGet(payload, "default", "0");
        string minVal = DictGet(payload, "min");
        string maxVal = DictGet(payload, "max");
        string unit = DictGet(payload, "unit", "[-]");
        string description = DictGet(payload, "description");

        string resultJson = "";

        ModifyProjectDat(hdbPath, (repo, variables, dbNameMap, rawDat) =>
        {
            string dbId = ResolveDbId(dbNameMap, database);

            // Check for duplicate name in same database
            foreach (var v in variables)
            {
                if (v == null || v is string) continue;
                if ((GetPropValue<string>(v, "DatabaseListId") ?? "").Equals(dbId, StringComparison.OrdinalIgnoreCase) &&
                    (GetPropValue<string>(v, "Name") ?? "").Equals(name, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"Variable '{name}' already exists in database '{database}'.");
            }

            // Find a template variable with matching type for cloning
            object template = null;
            string templateGuid = "";
            int templateIdx = -1;
            for (int i = 0; i < variables.Count; i++)
            {
                var v = variables[i];
                if (v == null || v is string) continue;
                if ((GetPropValue<string>(v, "VarType") ?? "").Equals(varType, StringComparison.OrdinalIgnoreCase))
                {
                    template = v;
                    templateGuid = GetPropValue<string>(v, "GUID") ?? "";
                    templateIdx = i;
                    break;
                }
            }
            if (template == null)
                throw new ArgumentException($"No existing variable with type '{varType}' found to use as template.");

            // Clone the template by deserializing the ORIGINAL project.dat bytes.
            // This fires the full deserialization pipeline (including [OnDeserialized]
            // callbacks at the dataLayer level) which properly reconstructs
            // [NonSerialized] fields like tLoadedTValues.
            object clone;
            using (var ms = new MemoryStream(rawDat))
            {
                var fmt = new BinaryFormatter();
                var clonedDataLayer = fmt.Deserialize(ms);
                var clonedRepo = GetProp(clonedDataLayer, "Repo");
                var clonedVars = GetProp(clonedRepo, "Variables") as IList;
                clone = clonedVars[templateIdx];
            }

            // Calculate next CommID and Idx
            int maxCommId = 0;
            int maxIdx = 0;
            foreach (var v in variables)
            {
                if (v == null || v is string) continue;
                int cid = GetPropValue<int>(v, "CommID");
                if (cid > maxCommId) maxCommId = cid;

                if ((GetPropValue<string>(v, "DatabaseListId") ?? "").Equals(dbId, StringComparison.OrdinalIgnoreCase))
                {
                    int idx = GetPropValue<int>(v, "Idx");
                    if (idx > maxIdx) maxIdx = idx;
                }
            }

            // Set properties on clone
            string newGuid = Guid.NewGuid().ToString();
            SetProp(clone, "Name", name);
            SetProp(clone, "ObjectAcronym", name);
            SetProp(clone, "DatabaseListId", dbId);
            SetProp(clone, "GUID", newGuid);
            SetProp(clone, "ObjectId", newGuid);
            SetProp(clone, "CommID", maxCommId + 1);
            SetProp(clone, "Idx", maxIdx + 1);
            SetProp(clone, "UnitEcu", unit);
            SetProp(clone, "Description", description);
            SetProp(clone, "Notes", "");
            SetProp(clone, "HstCode", "");

            // Set min/max
            if (string.IsNullOrEmpty(minVal))
            {
                // Use type defaults from the TType
                var ttype = GetProp(clone, "TType");
                if (ttype != null)
                    minVal = GetPropValue<string>(ttype, "Min") ?? "0";
            }
            if (string.IsNullOrEmpty(maxVal))
            {
                var ttype = GetProp(clone, "TType");
                if (ttype != null)
                    maxVal = GetPropValue<string>(ttype, "Max") ?? "0";
            }
            SetProp(clone, "Min", minVal);
            SetProp(clone, "Max", maxVal);

            // Update Detail min/max
            var detail = GetProp(clone, "Detail");
            if (detail != null)
            {
                SetProp(detail, "Min", minVal);
                SetProp(detail, "Max", maxVal);
            }

            // Update default value in Values[0]
            // The Values property getter may fail on BinaryFormatter clones because
            // TVar.get_TValues() tries to wrap a null internal list in ObservableCollection.
            // Access the underlying field directly to avoid this.
            try
            {
                // Try internal field first (_tValues, _values, etc.)
                IList vals = null;
                foreach (var fieldName in new[] { "_tValues", "_values", "tValues", "values" })
                {
                    var field = clone.GetType().GetField(fieldName,
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (field != null)
                    {
                        vals = field.GetValue(clone) as IList;
                        if (vals != null) break;
                    }
                }
                // Fall back to property if field access didn't work
                if (vals == null)
                    vals = GetProp(clone, "Values") as IList;

                if (vals != null && vals.Count > 0)
                {
                    var val0 = vals[0];
                    SetProp(val0, "DisplayValue", defaultValue);

                    // Update GUID for the value
                    string valGuid = Guid.NewGuid().ToString();
                    SetProp(val0, "GUID", valGuid);

                    // Update DatasetArrayValues[0]
                    var dsValues = GetProp(val0, "DatasetArrayValues") as IList;
                    if (dsValues != null && dsValues.Count > 0)
                    {
                        SetProp(dsValues[0], "Value", defaultValue);
                        SetProp(dsValues[0], "GUID", valGuid);
                    }

                    // Update Detail.Defaults[0]
                    var valDetail = GetProp(val0, "Detail");
                    if (valDetail != null)
                    {
                        var defaults = GetProp(valDetail, "Defaults") as IList;
                        if (defaults != null && defaults.Count > 0)
                        {
                            SetProp(defaults[0], "Value", defaultValue);
                            SetProp(defaults[0], "GUID", valGuid);
                        }
                    }
                }
            }
            catch
            {
                // Values collection not accessible on clone — the serialized data
                // from the template is preserved, default value will match template.
            }

            // Add to collection
            variables.Add(clone);

            resultJson = JsonSerializer.Serialize(new
            {
                status = "ok",
                name,
                database,
                var_type = varType,
                default_value = defaultValue,
                min = minVal,
                max = maxVal,
                unit,
                comm_id = maxCommId + 1,
                idx = maxIdx + 1,
                guid = newGuid,
            }, new JsonSerializerOptions { WriteIndented = true });
        });

        return resultJson;
    }

    static string DbUpdateVar(string hdbPath, string stdinJson)
    {
        var payload = JsonSerializer.Deserialize<Dictionary<string, string>>(stdinJson)
            ?? throw new ArgumentException("Invalid JSON input");

        string database = DictGet(payload, "database");
        if (string.IsNullOrEmpty(database)) throw new ArgumentException("'database' is required");
        string varName = DictGet(payload, "variable");
        if (string.IsNullOrEmpty(varName)) throw new ArgumentException("'variable' is required");

        string resultJson = "";

        ModifyProjectDat(hdbPath, (repo, variables, dbNameMap, rawDat) =>
        {
            string dbId = ResolveDbId(dbNameMap, database);
            var tvar = FindVar(variables, dbId, varName);

            // Update fields that are provided
            if (payload.ContainsKey("default"))
            {
                string defVal = payload["default"];
                try
                {
                    var vals = GetProp(tvar, "Values") as IList;
                    if (vals != null && vals.Count > 0)
                    {
                        SetProp(vals[0], "DisplayValue", defVal);
                        var dsValues = GetProp(vals[0], "DatasetArrayValues") as IList;
                        if (dsValues != null && dsValues.Count > 0)
                            SetProp(dsValues[0], "Value", defVal);
                        var valDetail = GetProp(vals[0], "Detail");
                        if (valDetail != null)
                        {
                            var defaults = GetProp(valDetail, "Defaults") as IList;
                            if (defaults != null && defaults.Count > 0)
                                SetProp(defaults[0], "Value", defVal);
                        }
                    }
                }
                catch
                {
                    // Values getter may fail — try Detail.Defaults directly
                    var detail = GetProp(tvar, "Detail");
                    if (detail != null)
                    {
                        var defaults = GetProp(detail, "Defaults") as IList;
                        if (defaults != null && defaults.Count > 0)
                            SetProp(defaults[0], "Value", defVal);
                    }
                }
            }

            if (payload.ContainsKey("min"))
            {
                SetProp(tvar, "Min", payload["min"]);
                var detail = GetProp(tvar, "Detail");
                if (detail != null) SetProp(detail, "Min", payload["min"]);
            }

            if (payload.ContainsKey("max"))
            {
                SetProp(tvar, "Max", payload["max"]);
                var detail = GetProp(tvar, "Detail");
                if (detail != null) SetProp(detail, "Max", payload["max"]);
            }

            if (payload.ContainsKey("unit"))
                SetProp(tvar, "UnitEcu", payload["unit"]);

            if (payload.ContainsKey("description"))
                SetProp(tvar, "Description", payload["description"]);

            resultJson = JsonSerializer.Serialize(new
            {
                status = "ok",
                name = GetPropValue<string>(tvar, "Name") ?? "",
                database,
                message = "Variable updated successfully.",
            }, new JsonSerializerOptions { WriteIndented = true });
        });

        return resultJson;
    }

    static string DbDeleteVar(string hdbPath, string stdinJson)
    {
        var payload = JsonSerializer.Deserialize<Dictionary<string, string>>(stdinJson)
            ?? throw new ArgumentException("Invalid JSON input");

        string database = DictGet(payload, "database");
        if (string.IsNullOrEmpty(database)) throw new ArgumentException("'database' is required");
        string varName = DictGet(payload, "variable");
        if (string.IsNullOrEmpty(varName)) throw new ArgumentException("'variable' is required");

        string resultJson = "";

        ModifyProjectDat(hdbPath, (repo, variables, dbNameMap, rawDat) =>
        {
            string dbId = ResolveDbId(dbNameMap, database);
            var tvar = FindVar(variables, dbId, varName);
            string name = GetPropValue<string>(tvar, "Name") ?? "";

            variables.Remove(tvar);

            resultJson = JsonSerializer.Serialize(new
            {
                status = "ok",
                name,
                database,
                message = "Variable deleted successfully.",
            }, new JsonSerializerOptions { WriteIndented = true });
        });

        return resultJson;
    }

    // -----------------------------------------------------------------------
    // Custom error add
    // -----------------------------------------------------------------------

    /// <summary>
    /// Modify both project.dat and Errors.dat in the HDB atomically.
    /// modifier receives: roots dict, rawBytes dict.
    /// </summary>
    static void ModifyHdb(string hdbPath, string[] datFiles, Action<Dictionary<string, object>, Dictionary<string, byte[]>> modifier)
    {
        var entries = new Dictionary<string, byte[]>();
        using (var zip = ZipFile.OpenRead(hdbPath))
        {
            foreach (var entry in zip.Entries)
            {
                using var ms = new MemoryStream();
                using var s = entry.Open();
                s.CopyTo(ms);
                entries[entry.FullName] = ms.ToArray();
            }
        }

        var roots = new Dictionary<string, object>();
        var rawBytes = new Dictionary<string, byte[]>();
        foreach (var datFile in datFiles)
        {
            rawBytes[datFile] = entries[datFile];
            using var ms = new MemoryStream(entries[datFile]);
            roots[datFile] = new BinaryFormatter().Deserialize(ms);
        }

        modifier(roots, rawBytes);

        // Force-initialize lazy fields on variables
        var repo = GetProp(roots["project.dat"], "Repo");
        var variables = repo != null ? GetProp(repo, "Variables") as IList : null;
        if (variables != null)
        {
            foreach (var v in variables)
            {
                if (v == null || v is string) continue;
                try { var _ = GetProp(v, "Values"); } catch { }
            }
        }

        // Serialize modified roots back
        foreach (var datFile in datFiles)
        {
            try
            {
                using var ms = new MemoryStream();
                new BinaryFormatter().Serialize(ms, roots[datFile]);
                entries[datFile] = ms.ToArray();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Serialization of {datFile} failed: {ex.Message}");
                if (ex.InnerException != null)
                    Console.Error.WriteLine($"  Inner: {ex.InnerException.Message}");
                throw;
            }
        }

        string backupPath = hdbPath + ".bak";
        if (!File.Exists(backupPath))
            File.Copy(hdbPath, backupPath);

        string tmpPath = hdbPath + ".tmp";
        try
        {
            using (var zipOut = ZipFile.Open(tmpPath, ZipArchiveMode.Create))
            {
                foreach (var kvp in entries)
                {
                    var entry = zipOut.CreateEntry(kvp.Key, CompressionLevel.Optimal);
                    using var s = entry.Open();
                    s.Write(kvp.Value, 0, kvp.Value.Length);
                }
            }
            File.Delete(hdbPath);
            File.Move(tmpPath, hdbPath);
        }
        catch
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
            throw;
        }
    }

    /// <summary>
    /// Resolve FMI name to GUID string from Repo.Fmis list.
    /// </summary>
    static string ResolveFmiGuid(IList fmis, string fmiName)
    {
        if (fmis == null) return "";
        foreach (var fmi in fmis)
        {
            if (fmi == null || fmi is string) continue;
            if ((GetPropValue<string>(fmi, "Name") ?? "").Equals(fmiName, StringComparison.OrdinalIgnoreCase))
                return GetPropValue<string>(fmi, "ObjectId") ?? GetPropValue<string>(fmi, "GUID") ?? "";
        }
        return "";
    }

    /// <summary>
    /// Resolve FMI extended name to GUID string from Repo.FmiExts list.
    /// </summary>
    static string ResolveFmiExtGuid(IList fmiExts, string fmiExtName)
    {
        if (fmiExts == null) return "";
        foreach (var fmiEx in fmiExts)
        {
            if (fmiEx == null || fmiEx is string) continue;
            if ((GetPropValue<string>(fmiEx, "Name") ?? "").Equals(fmiExtName, StringComparison.OrdinalIgnoreCase))
                return GetPropValue<string>(fmiEx, "ObjectId") ?? GetPropValue<string>(fmiEx, "GUID") ?? "";
        }
        return "";
    }

    /// <summary>
    /// Extract integer FMI value from name like "FMI_31_CONDITION_EXISTS" → 31.
    /// </summary>
    static int ExtractFmiValue(string fmiName)
    {
        if (string.IsNullOrEmpty(fmiName)) return 31;
        var parts = fmiName.Split('_');
        for (int i = 1; i < parts.Length; i++)
        {
            if (int.TryParse(parts[i], out int val)) return val;
        }
        return 31;
    }

    /// <summary>
    /// Extract integer FMI extended value. FMIEX_GLOBAL/FMIEX_NONE → 255.
    /// </summary>
    static int ExtractFmiExtValue(string fmiExtName)
    {
        if (string.IsNullOrEmpty(fmiExtName)) return 255;
        if (fmiExtName.Equals("FMIEX_GLOBAL", StringComparison.OrdinalIgnoreCase) ||
            fmiExtName.Equals("FMIEX_NONE", StringComparison.OrdinalIgnoreCase))
            return 255;
        var parts = fmiExtName.Split('_');
        for (int i = 1; i < parts.Length; i++)
        {
            if (int.TryParse(parts[i], out int val)) return val;
        }
        return 255;
    }

    static string CustomErrorAdd(string hdbPath, string stdinJson)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(stdinJson);

        string template = payload.GetProperty("template").GetString() ?? "";
        string dmName = payload.GetProperty("dm_name").GetString() ?? "";
        int bit = payload.GetProperty("bit").GetInt32();
        int spn = payload.GetProperty("spn").GetInt32();
        string blockName = payload.TryGetProperty("block_name", out var bn) ? (bn.GetString() ?? "") : "";
        string description = payload.TryGetProperty("description", out var d) ? (d.GetString() ?? "") : "";
        int severity = payload.TryGetProperty("severity", out var sv) ? sv.GetInt32() : 3;
        string fmiName = payload.TryGetProperty("fmi", out var fm) ? (fm.GetString() ?? "FMI_31_CONDITION_EXISTS") : "FMI_31_CONDITION_EXISTS";
        string fmiExtName = payload.TryGetProperty("fmi_extended", out var fmx) ? (fmx.GetString() ?? "FMIEX_GLOBAL") : "FMIEX_GLOBAL";
        int setDebounceMs = payload.TryGetProperty("set_debounce_ms", out var sdm) ? sdm.GetInt32() : 500;
        int releaseDebounceMs = payload.TryGetProperty("release_debounce_ms", out var rdm) ? rdm.GetInt32() : 0;
        int setThreshold = payload.TryGetProperty("set_threshold", out var st) ? st.GetInt32() : 500;
        int releaseThreshold = payload.TryGetProperty("release_threshold", out var rt) ? rt.GetInt32() : 1000;

        if (string.IsNullOrEmpty(template)) throw new ArgumentException("'template' is required");
        if (string.IsNullOrEmpty(dmName)) throw new ArgumentException("'dm_name' is required");
        if (bit < 0 || bit > 7) throw new ArgumentException("'bit' must be 0-7");
        if (spn <= 0) throw new ArgumentException("'spn' must be > 0");

        int defaultFmi = ExtractFmiValue(fmiName);
        int defaultFmiEx = ExtractFmiExtValue(fmiExtName);

        string resultJson = "";

        ModifyHdb(hdbPath, new[] { "project.dat", "Errors.dat" }, (roots, rawBytes) =>
        {
            // Access project.dat structures
            var projectRoot = roots["project.dat"];
            var repo = GetProp(projectRoot, "Repo")
                ?? throw new InvalidOperationException("Repo not found");
            var repoProject = GetProp(repo, "RepoProject");
            var detail = GetProp(repoProject, "Detail");
            var dmData = GetProp(detail, "DetectionMethodData");
            var customSet = GetProp(dmData, "Custom");
            var customDMs = GetProp(customSet, "DetectionMethods") as IList;
            var customTemplates = GetProp(customSet, "ErrorTemplates") as IList;
            var repoDMs = GetProp(repo, "DetectionMethod") as IList;
            var fmis = GetProp(repo, "Fmis") as IList;
            var fmiExts = GetProp(repo, "FmiExts") as IList;
            var revSofts = GetProp(repo, "RevisionSoftwares") as IList;
            var ecu = GetProp(revSofts[0], "Ecu");
            var tblocks = GetProp(ecu, "TBlocks") as IList;

            // Access Errors.dat
            var errRoot = roots["Errors.dat"];
            var errors = GetProp(errRoot, "Errors") as IList
                ?? throw new InvalidOperationException("Errors.dat: Errors not found");

            // Validate SPN uniqueness
            foreach (var err in errors)
            {
                if (err == null || err is string) continue;
                if (GetPropValue<int>(err, "Spn") == spn)
                    throw new ArgumentException($"SPN {spn} already exists.");
            }

            // Validate DM name uniqueness
            foreach (var dm in customDMs)
            {
                if (dm == null || dm is string) continue;
                var det = GetPropValue<string>(dm, "Detection") ?? "";
                if (det.Equals(dmName, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"DM name '{dmName}' already exists.");
            }

            // Resolve FMI GUIDs
            string fmiGuid = ResolveFmiGuid(fmis, fmiName);
            string fmiExtGuid = ResolveFmiExtGuid(fmiExts, fmiExtName);

            // --- Find or create template ---
            object targetTemplate = null;
            bool newTemplate = false;
            int sourceTemplateIdx = -1;
            int sourceBlockIdx = -1;

            // Search for existing template
            for (int i = 0; i < customTemplates.Count; i++)
            {
                var t = customTemplates[i];
                if (t == null || t is string) continue;
                if (sourceTemplateIdx < 0) sourceTemplateIdx = i; // first valid template
                var tType = GetPropValue<string>(t, "Type") ?? "";
                if (tType.Equals(template, StringComparison.OrdinalIgnoreCase))
                {
                    targetTemplate = t;
                    break;
                }
            }

            // Find source ERR TBlock index (needed for both new and existing template paths)
            for (int i = 0; i < tblocks.Count; i++)
            {
                if (tblocks[i] == null || tblocks[i] is string) continue;
                if ((GetPropValue<string>(tblocks[i], "Type") ?? "") == "ERR")
                {
                    sourceBlockIdx = i;
                    break;
                }
            }

            // --- Create new template if not found ---
            if (targetTemplate == null)
            {
                if (sourceTemplateIdx < 0)
                    throw new InvalidOperationException("No existing error templates to clone from.");
                if (string.IsNullOrEmpty(blockName))
                    throw new ArgumentException("'block_name' is required when creating a new template.");

                // Clone template (stays in live graph)
                var srcTemplate = customTemplates[sourceTemplateIdx];
                try { targetTemplate = Activator.CreateInstance(srcTemplate.GetType()); }
                catch { targetTemplate = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(srcTemplate.GetType()); }
                foreach (var prop in srcTemplate.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0) continue;
                    if (prop.Name == "Error" || prop.Name == "LinkedBlockIds" || prop.Name == "LinkedBlocks") continue;
                    try { prop.SetValue(targetTemplate, prop.GetValue(srcTemplate)); } catch { }
                }

                string tmplGuid = Guid.NewGuid().ToString();
                SetProp(targetTemplate, "Type", template);
                try { SetProp(targetTemplate, "Name", template); } catch { }
                try { SetProp(targetTemplate, "ObjectId", tmplGuid); } catch { }
                try { SetProp(targetTemplate, "GUID", tmplGuid); } catch { }
                SetProp(targetTemplate, "Description", "");
                try { SetProp(targetTemplate, "PinType", "DEFAULT"); } catch { }

                // Create empty LinkedBlockIds list (populated after TBlock creation)
                var srcLinkedIds = GetProp(srcTemplate, "LinkedBlockIds") as IList;
                if (srcLinkedIds != null)
                {
                    var newLinkedIds = (IList)Activator.CreateInstance(srcLinkedIds.GetType());
                    try { SetProp(targetTemplate, "LinkedBlockIds", newLinkedIds); } catch { }
                }

                // Create ONLY 1 DM template entry (for the active bit) — matching PDT behavior
                var srcError = GetProp(srcTemplate, "Error") as IList;
                if (srcError != null)
                {
                    var newErrorList = (IList)Activator.CreateInstance(srcError.GetType());
                    try { SetProp(targetTemplate, "Error", newErrorList); } catch { }

                    // Find source DM template at bit 0 as template for structure
                    object srcDmTmpl0 = null;
                    foreach (var dm in srcError)
                    {
                        if (dm != null && !(dm is string)) { srcDmTmpl0 = dm; break; }
                    }
                    if (srcDmTmpl0 != null)
                    {
                        var newDmTmpl = Activator.CreateInstance(srcDmTmpl0.GetType());
                        foreach (var prop in srcDmTmpl0.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0) continue;
                            try { prop.SetValue(newDmTmpl, prop.GetValue(srcDmTmpl0)); } catch { }
                        }
                        SetProp(newDmTmpl, "Bit", bit);
                        SetProp(newDmTmpl, "DetectionMethodName", $"DM_ERR_{bit:D2}");
                        SetProp(newDmTmpl, "Detection", dmName);
                        SetProp(newDmTmpl, "DetectionVm", dmName);
                        try { SetProp(newDmTmpl, "Description", description); } catch { }
                        SetProp(newDmTmpl, "DefaultFmi", defaultFmi);
                        SetProp(newDmTmpl, "DefaultFmiEx", defaultFmiEx);

                        newErrorList.Add(newDmTmpl);
                        customDMs.Add(newDmTmpl);
                    }
                }

                customTemplates.Add(targetTemplate);
                newTemplate = true;
            }

            // --- For existing template: validate bit, create new DM template ---
            string dmGuid = "";
            if (!newTemplate)
            {
                // Validate bit is not already in use
                var existingError = GetProp(targetTemplate, "Error") as IList;
                if (existingError != null)
                {
                    foreach (var dmTmpl in existingError)
                    {
                        if (dmTmpl == null || dmTmpl is string) continue;
                        if (GetPropValue<int>(dmTmpl, "Bit") == bit)
                        {
                            var existDet = GetPropValue<string>(dmTmpl, "Detection") ?? "";
                            if (!string.IsNullOrEmpty(existDet) && existDet != $"DM_ERR_{bit:D2}")
                                throw new ArgumentException($"Bit {bit} already used by '{existDet}' in template '{template}'.");
                        }
                    }
                }

                // Clone a DM template using Activator (stays in live graph)
                object sourceDm = null;
                for (int i = 0; i < customDMs.Count; i++)
                {
                    if (customDMs[i] != null && !(customDMs[i] is string))
                    { sourceDm = customDMs[i]; break; }
                }
                if (sourceDm == null)
                    throw new InvalidOperationException("No existing DM templates to clone from.");

                var newDm = Activator.CreateInstance(sourceDm.GetType());
                foreach (var prop in sourceDm.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0) continue;
                    try { prop.SetValue(newDm, prop.GetValue(sourceDm)); } catch { }
                }
                dmGuid = Guid.NewGuid().ToString();
                SetProp(newDm, "Detection", dmName);
                SetProp(newDm, "DetectionVm", dmName);
                SetProp(newDm, "DetectionMethodName", dmName);
                SetProp(newDm, "Bit", bit);
                try { SetProp(newDm, "GroupName", template); } catch { }
                try { SetProp(newDm, "Description", description); } catch { }
                try { SetProp(newDm, "ObjectId", dmGuid); } catch { }
                try { SetProp(newDm, "GUID", dmGuid); } catch { }
                SetProp(newDm, "DefaultFmi", defaultFmi);
                SetProp(newDm, "DefaultFmiEx", defaultFmiEx);

                customDMs.Add(newDm);

                // Add to template's Error list
                if (existingError != null)
                    existingError.Add(newDm);
            }

            // --- Clone and add error entry from Errors.dat ---
            int sourceErrIdx = -1;
            for (int i = 0; i < errors.Count; i++)
            {
                if (errors[i] == null || errors[i] is string) continue;
                if (sourceErrIdx < 0) sourceErrIdx = i;
                // Prefer a custom error (SPN >= 15000) as template
                if (GetPropValue<int>(errors[i], "Spn") >= 15000)
                { sourceErrIdx = i; break; }
            }
            if (sourceErrIdx < 0) throw new InvalidOperationException("No existing errors to clone from.");

            object newError;
            using (var ms = new MemoryStream(rawBytes["Errors.dat"]))
            {
                var clonedErrRoot = new BinaryFormatter().Deserialize(ms);
                var clonedErrors = GetProp(clonedErrRoot, "Errors") as IList;
                newError = clonedErrors[sourceErrIdx];
            }

            string errGuid = Guid.NewGuid().ToString();
            SetProp(newError, "Spn", spn);
            SetProp(newError, "Description", description);
            SetProp(newError, "Severity", severity);
            SetProp(newError, "ObjectId", errGuid);
            try { SetProp(newError, "GUID", errGuid); } catch { }
            if (!string.IsNullOrEmpty(fmiGuid))
                SetProp(newError, "Fmi", fmiGuid);
            if (!string.IsNullOrEmpty(fmiExtGuid))
                SetProp(newError, "FmiExtended", fmiExtGuid);
            // DetectionMethod GUID set later if TBlock is created
            if (!string.IsNullOrEmpty(dmGuid))
                SetProp(newError, "DetectionMethod", dmGuid);

            var setProps = GetProp(newError, "SetProperties");
            if (setProps != null)
            {
                SetProp(setProps, "DebounceTime", setDebounceMs);
                SetProp(setProps, "IsDebounceEnabled", setDebounceMs > 0);
                SetProp(setProps, "Threshold", setThreshold);
            }
            var releaseProps = GetProp(newError, "ReleaseProperties");
            if (releaseProps != null)
            {
                SetProp(releaseProps, "DebounceTime", releaseDebounceMs);
                SetProp(releaseProps, "IsDebounceEnabled", releaseDebounceMs > 0);
                SetProp(releaseProps, "Threshold", releaseThreshold);
            }

            errors.Add(newError);

            // --- Create or update TBlock ---
            if (!string.IsNullOrEmpty(blockName))
            {
                if (newTemplate)
                {
                    // Clone TBlock using Activator within the LIVE graph.
                    // Deep-clone reference-type sub-objects to avoid shared references.
                    if (sourceBlockIdx < 0)
                        throw new InvalidOperationException("No existing ERR TBlock to clone from.");

                    object sourceBlock = tblocks[sourceBlockIdx];
                    string blockGuid = Guid.NewGuid().ToString();

                    var newBlock = Activator.CreateInstance(sourceBlock.GetType());

                    // Copy scalar-only top-level properties
                    var skipProps = new HashSet<string> {
                        "TEcu", "TBlockParams", "TBlockParamSections", "TDetectionMethods",
                        "DetectionMethods", "Ports", "TPins", "TSignalElement", "TComponents",
                        "Detail", "TSoftwareModule", "Blueprint", "BaseBlueprint", "EcuBlueprint",
                        "Message", "UpdatingTemplate", "TBlock", "EcuCan"
                    };
                    foreach (var prop in sourceBlock.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0) continue;
                        if (skipProps.Contains(prop.Name)) continue;
                        try { prop.SetValue(newBlock, prop.GetValue(sourceBlock)); } catch { }
                    }

                    SetProp(newBlock, "Name", blockName);
                    SetProp(newBlock, "Key", $"[ERR].[{blockName}]");
                    SetProp(newBlock, "ObjectId", blockGuid);
                    try { SetProp(newBlock, "GUID", blockGuid); } catch { }
                    try { SetProp(newBlock, "TEcu", ecu); } catch { }
                    try { SetProp(newBlock, "GUIDBlueprint", "00000000-0000-0000-0000-000000000000"); } catch { }

                    // TBlock.Detail: shared (Activator nonPublic ctor causes serialization issues)

                    // (diagnostic code removed)
                    if (false) {
                        var diagSections = GetProp(sourceBlock, "TBlockParamSections") as IList;
                        object diagVd = null;
                        if (diagSections != null)
                        {
                            foreach (var s in diagSections)
                            {
                                if (s == null || s is string) continue;
                                var ps = GetProp(s, "TBlockParams") as IList;
                                if (ps == null) continue;
                                foreach (var p in ps)
                                {
                                    if (p == null || p is string) continue;
                                    diagVd = GetProp(p, "ValueData");
                                    if (diagVd != null) break;
                                }
                                if (diagVd != null) break;
                            }
                        }
                        if (diagVd != null)
                        {
                            var vdType = diagVd.GetType();
                            Console.Error.WriteLine($"DIAG: ValueData type = {vdType.FullName}");
                            Console.Error.WriteLine($"DIAG: IsGenericType = {vdType.IsGenericType}");
                            Console.Error.WriteLine($"DIAG: IsSerializable = {vdType.IsSerializable}");

                            // List constructors
                            foreach (var ctor in vdType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                            {
                                var ctorParams = ctor.GetParameters();
                                Console.Error.WriteLine($"DIAG: Constructor({string.Join(", ", ctorParams.Select(cp => cp.ParameterType.Name + " " + cp.Name))})");
                            }

                            // Try Activator
                            try
                            {
                                var test1 = Activator.CreateInstance(vdType, true);
                                Console.Error.WriteLine("DIAG: Activator.CreateInstance(type, true) = OK");
                            }
                            catch (Exception ex) { Console.Error.WriteLine($"DIAG: Activator.CreateInstance(type, true) FAILED: {ex.InnerException?.Message ?? ex.Message}"); }

                            try
                            {
                                var test2 = Activator.CreateInstance(vdType);
                                Console.Error.WriteLine("DIAG: Activator.CreateInstance(type) = OK");
                            }
                            catch (Exception ex) { Console.Error.WriteLine($"DIAG: Activator.CreateInstance(type) FAILED: {ex.InnerException?.Message ?? ex.Message}"); }

                            // Try BinaryFormatter round-trip on the individual object
                            try
                            {
                                using var ms = new MemoryStream();
                                var fmt = new BinaryFormatter();
                                fmt.Serialize(ms, diagVd);
                                ms.Position = 0;
                                var test3 = fmt.Deserialize(ms);
                                Console.Error.WriteLine($"DIAG: BinaryFormatter clone = OK (type={test3.GetType().Name})");
                                // Check Value property
                                var valProp = test3.GetType().GetProperty("Value");
                                if (valProp != null)
                                {
                                    var val = valProp.GetValue(test3);
                                    Console.Error.WriteLine($"DIAG: Cloned Value = {val}");
                                }
                            }
                            catch (Exception ex) { Console.Error.WriteLine($"DIAG: BinaryFormatter clone FAILED: {ex.InnerException?.Message ?? ex.Message}"); }

                            // Try same for BlockParameterDetail
                            object diagBpd = null;
                            foreach (var s in diagSections)
                            {
                                if (s == null || s is string) continue;
                                var ps = GetProp(s, "TBlockParams") as IList;
                                if (ps == null) continue;
                                foreach (var p in ps)
                                {
                                    if (p == null || p is string) continue;
                                    diagBpd = GetProp(p, "BlockParameterDetail");
                                    if (diagBpd != null) break;
                                }
                                if (diagBpd != null) break;
                            }
                            if (diagBpd != null)
                            {
                                try
                                {
                                    using var ms = new MemoryStream();
                                    var fmt = new BinaryFormatter();
                                    fmt.Serialize(ms, diagBpd);
                                    ms.Position = 0;
                                    var test4 = fmt.Deserialize(ms);
                                    Console.Error.WriteLine($"DIAG: BF clone BlockParameterDetail = OK");
                                }
                                catch (Exception ex) { Console.Error.WriteLine($"DIAG: BF clone BlockParameterDetail FAILED: {ex.Message}"); }
                            }

                            // Try same for TBlockParamSectionDetail
                            foreach (var s in diagSections)
                            {
                                if (s == null || s is string) continue;
                                var diagSd = GetProp(s, "Detail");
                                if (diagSd != null)
                                {
                                    try
                                    {
                                        using var ms = new MemoryStream();
                                        var fmt = new BinaryFormatter();
                                        fmt.Serialize(ms, diagSd);
                                        ms.Position = 0;
                                        var test5 = fmt.Deserialize(ms);
                                        Console.Error.WriteLine($"DIAG: BF clone SectionDetail = OK");
                                    }
                                    catch (Exception ex) { Console.Error.WriteLine($"DIAG: BF clone SectionDetail FAILED: {ex.Message}"); }
                                    break;
                                }
                            }

                            // Try same for TBlockDetail
                            var diagBlockDetail = GetProp(sourceBlock, "Detail");
                            if (diagBlockDetail != null)
                            {
                                try
                                {
                                    using var ms = new MemoryStream();
                                    var fmt = new BinaryFormatter();
                                    fmt.Serialize(ms, diagBlockDetail);
                                    ms.Position = 0;
                                    var test6 = fmt.Deserialize(ms);
                                    Console.Error.WriteLine($"DIAG: BF clone BlockDetail = OK");
                                }
                                catch (Exception ex) { Console.Error.WriteLine($"DIAG: BF clone BlockDetail FAILED: {ex.Message}"); }
                            }

                            // Try TDetectionMethodDetail
                            var diagDMs = GetProp(sourceBlock, "TDetectionMethods") as IList;
                            if (diagDMs != null && diagDMs.Count > 0)
                            {
                                var diagDmDetail = GetProp(diagDMs[0], "Detail");
                                if (diagDmDetail != null)
                                {
                                    try
                                    {
                                        using var ms = new MemoryStream();
                                        var fmt = new BinaryFormatter();
                                        fmt.Serialize(ms, diagDmDetail);
                                        ms.Position = 0;
                                        var test7 = fmt.Deserialize(ms);
                                        Console.Error.WriteLine($"DIAG: BF clone DMDetail = OK");
                                    }
                                    catch (Exception ex) { Console.Error.WriteLine($"DIAG: BF clone DMDetail FAILED: {ex.Message}"); }
                                }
                            }
                        }
                    }
                    // --- END DIAGNOSTIC ---

                    // Clone TBlockParamSections
                    var srcSections = GetProp(sourceBlock, "TBlockParamSections") as IList;
                    if (srcSections != null)
                    {
                        var newSections = (IList)Activator.CreateInstance(srcSections.GetType());
                        try { SetProp(newBlock, "TBlockParamSections", newSections); } catch { }

                        foreach (var srcSection in srcSections)
                        {
                            if (srcSection == null || srcSection is string) continue;
                            var newSection = Activator.CreateInstance(srcSection.GetType());
                            foreach (var prop in srcSection.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                            {
                                if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0) continue;
                                if (prop.Name == "TBlock" || prop.Name == "TBlockParams") continue;
                                try { prop.SetValue(newSection, prop.GetValue(srcSection)); } catch { }
                            }
                            try { SetProp(newSection, "GUID", Guid.NewGuid().ToString()); } catch { }
                            try { SetProp(newSection, "TBlock", newBlock); } catch { }
                            // Section.Detail: shared (Detail types cause deserialization NullRef when created empty)

                            // Clone TBlockParams within section
                            var srcParams = GetProp(srcSection, "TBlockParams") as IList;
                            if (srcParams != null)
                            {
                                var newParams = (IList)Activator.CreateInstance(srcParams.GetType());
                                try { SetProp(newSection, "TBlockParams", newParams); } catch { }

                                foreach (var srcParam in srcParams)
                                {
                                    if (srcParam == null || srcParam is string) continue;
                                    var newParam = Activator.CreateInstance(srcParam.GetType());
                                    foreach (var prop in srcParam.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                                    {
                                        if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0) continue;
                                        if (prop.Name == "TBlock" || prop.Name == "TBlockParamSection"
                                            || prop.Name == "ValueData" || prop.Name == "BlockParameterDetail") continue;
                                        try { prop.SetValue(newParam, prop.GetValue(srcParam)); } catch { }
                                    }
                                    try { SetProp(newParam, "GUID", Guid.NewGuid().ToString()); } catch { }
                                    try { SetProp(newParam, "TBlock", newBlock); } catch { }
                                    try { SetProp(newParam, "TBlockParamSection", newSection); } catch { }

                                    // Create independent ValueData via constructor(TBlockParam)
                                    var srcVd = GetProp(srcParam, "ValueData");
                                    if (srcVd != null)
                                    {
                                        try
                                        {
                                            var newVd = Activator.CreateInstance(srcVd.GetType(), new object[] { newParam });
                                            foreach (var vp in srcVd.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                                            {
                                                if (!vp.CanRead || !vp.CanWrite || vp.GetIndexParameters().Length > 0) continue;
                                                try { vp.SetValue(newVd, vp.GetValue(srcVd)); } catch { }
                                            }
                                            SetProp(newParam, "ValueData", newVd);
                                        }
                                        catch { /* fallback: leave shared */ }
                                    }

                                                    // BlockParameterDetail: shared (empty Detail objects cause deserialization NullRef)

                                    if ((GetPropValue<string>(newParam, "Name") ?? "") == "BLOCK_NAME")
                                    {
                                        SetProp(newParam, "ValueText", blockName);
                                        var vd = GetProp(newParam, "ValueData");
                                        if (vd != null) try { SetProp(vd, "Value", blockName); } catch { }
                                    }

                                    newParams.Add(newParam);
                                }
                            }

                            newSections.Add(newSection);
                        }
                    }

                    // Clone TDetectionMethods
                    var srcDMsList = GetProp(sourceBlock, "TDetectionMethods") as IList;
                    if (srcDMsList != null)
                    {
                        var newDMsList = (IList)Activator.CreateInstance(srcDMsList.GetType());
                        try { SetProp(newBlock, "TDetectionMethods", newDMsList); } catch { }
                        try { SetProp(newBlock, "DetectionMethods", newDMsList); } catch { }

                        foreach (var srcBdm in srcDMsList)
                        {
                            if (srcBdm == null || srcBdm is string) continue;
                            var newBdm = Activator.CreateInstance(srcBdm.GetType());
                            foreach (var prop in srcBdm.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                            {
                                if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0) continue;
                                if (prop.Name == "TBlock" || prop.Name == "TError" || prop.Name == "Error") continue;
                                try { prop.SetValue(newBdm, prop.GetValue(srcBdm)); } catch { }
                            }
                            int bdmBit = GetPropValue<int>(newBdm, "Bit");
                            string bdmGuid = Guid.NewGuid().ToString();
                            try { SetProp(newBdm, "ObjectId", bdmGuid); } catch { }
                            try { SetProp(newBdm, "GUID", bdmGuid); } catch { }
                            SetProp(newBdm, "BlockObjectId", blockGuid);
                            try { SetProp(newBdm, "TBlock", newBlock); } catch { }
                            // DM.Detail: shared

                            if (bdmBit == bit)
                                SetProp(newBdm, "CustomName", dmName);
                            else
                                SetProp(newBdm, "CustomName", $"DM_ERR_{bdmBit:D2}");

                            newDMsList.Add(newBdm);
                            repoDMs.Add(newBdm);

                            if (bdmBit == bit)
                                SetProp(newError, "DetectionMethod", bdmGuid);
                        }
                    }

                    tblocks.Add(newBlock);

                    // Register in Repo.Blocks (flat index used by PDT)
                    var repoBlocks = GetProp(repo, "Blocks") as IList;
                    if (repoBlocks != null)
                        repoBlocks.Add(newBlock);

                    // Register TBlockParamSections in Repo.BlockParamSections
                    var repoSections = GetProp(repo, "BlockParamSections") as IList;
                    var blockSections = GetProp(newBlock, "TBlockParamSections") as IList;
                    if (repoSections != null && blockSections != null)
                    {
                        foreach (var s in blockSections)
                        {
                            if (s != null && !(s is string))
                                repoSections.Add(s);
                        }
                    }

                    // Link template
                    var linkedIds = GetProp(targetTemplate, "LinkedBlockIds") as IList;
                    if (linkedIds != null)
                        linkedIds.Add(Guid.Parse(blockGuid));

                    resultJson = JsonSerializer.Serialize(new
                    {
                        status = "ok",
                        spn,
                        dm_name = dmName,
                        template,
                        block_name = blockName,
                        tblock_guid = blockGuid,
                        error_guid = errGuid,
                        new_block = true,
                        message = $"Created new error block '{template}' with TBlock '{blockName}' and SPN {spn}."
                    }, new JsonSerializerOptions { WriteIndented = true });
                }
                else
                {
                    // Existing template: find TBlock by name and update DM CustomName
                    for (int i = 0; i < tblocks.Count; i++)
                    {
                        if (tblocks[i] == null || tblocks[i] is string) continue;
                        if ((GetPropValue<string>(tblocks[i], "Name") ?? "") == blockName)
                        {
                            var existDMs = GetProp(tblocks[i], "TDetectionMethods") as IList;
                            if (existDMs != null)
                            {
                                foreach (var bdm in existDMs)
                                {
                                    if (bdm == null || bdm is string) continue;
                                    if (GetPropValue<int>(bdm, "Bit") == bit)
                                    {
                                        SetProp(bdm, "CustomName", dmName);
                                        // Update error to point to this TBlock DM
                                        string bdmGuid = GetPropValue<string>(bdm, "ObjectId") ?? "";
                                        if (!string.IsNullOrEmpty(bdmGuid))
                                            SetProp(newError, "DetectionMethod", bdmGuid);
                                        break;
                                    }
                                }
                            }
                            break;
                        }
                    }

                    resultJson = JsonSerializer.Serialize(new
                    {
                        status = "ok",
                        spn,
                        dm_name = dmName,
                        template,
                        block_name = blockName,
                        error_guid = errGuid,
                        new_block = false,
                        message = $"Added DM '{dmName}' at bit {bit} to existing template '{template}' with SPN {spn}."
                    }, new JsonSerializerOptions { WriteIndented = true });
                }
            }
            else
            {
                // No block_name: just template + DM + error (no TBlock)
                resultJson = JsonSerializer.Serialize(new
                {
                    status = "ok",
                    spn,
                    dm_name = dmName,
                    template,
                    error_guid = errGuid,
                    new_block = false,
                    message = $"Added error SPN {spn} with DM '{dmName}' to template '{template}'."
                }, new JsonSerializerOptions { WriteIndented = true });
            }
        });

        return resultJson;
    }

    /// <summary>
    /// Deep-clone a reference-type object using GetUninitializedObject.
    /// Handles generic types like TBlockParamData&lt;T&gt; that Activator can't create.
    /// Copies all public properties and initializes IList fields that are null.
    /// </summary>

    // -----------------------------------------------------------------------
    // Reflection helpers (existing)
    // -----------------------------------------------------------------------

    static object? GetProp(object obj, string name)
    {
        return obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj);
    }

    static T GetPropValue<T>(object obj, string name)
    {
        var val = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj);
        if (val is T typed) return typed;
        if (val != null)
        {
            // Handle Guid-to-string conversion
            if (typeof(T) == typeof(string) && val is Guid g)
                return (T)(object)g.ToString();
            try { return (T)Convert.ChangeType(val, typeof(T)); } catch { }
        }
        return default!;
    }
}

// ---------------------------------------------------------------------------
// Generic recursive object walker
// ---------------------------------------------------------------------------

static class ObjectDumper
{
    private const int MaxDepth = 20;
    private const int MaxCollectionItems = 500;

    // Namespaces to skip (UI/threading types that carry no config data)
    private static readonly HashSet<string> SkipNamespaces = new HashSet<string>
    {
        "System.Windows.Threading",
        "System.Windows.Media",
        "System.Windows.Controls",
        "System.ComponentModel",
    };

    public static object Dump(object obj)
    {
        return DumpInternal(obj, MaxDepth, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    private static object DumpInternal(object obj, int depth, HashSet<object> visited)
    {
        if (obj == null)
            return null;

        var type = obj.GetType();

        // Primitives and simple types — return directly
        if (obj is string s) return s;
        if (obj is bool) return obj;
        if (type.IsPrimitive) return obj;
        if (obj is decimal d) return d;
        if (obj is DateTime dt) return dt.ToString("o");
        if (obj is DateTimeOffset dto) return dto.ToString("o");
        if (obj is TimeSpan ts) return ts.ToString();
        if (obj is Guid g) return g.ToString();
        if (obj is byte[] bytes) return Convert.ToBase64String(bytes);
        if (type.IsEnum) return obj.ToString();
        if (obj is Uri uri) return uri.ToString();
        if (obj is Type t) return t.FullName;

        // Depth guard
        if (depth <= 0)
            return "$max-depth";

        // Already-seen detection (prevents exponential blowup on shared objects)
        // Objects stay in visited permanently — revisits get "$seen: TypeName"
        if (!type.IsValueType)
        {
            if (!visited.Add(obj))
                return "$seen: " + type.Name;
        }

        // IDictionary — dump as key-value pairs
        if (obj is IDictionary dict)
        {
            var result = new Dictionary<string, object>();
            result["$type"] = type.Name;
            int count = 0;
            foreach (DictionaryEntry entry in dict)
            {
                if (count++ >= MaxCollectionItems) break;
                var key = entry.Key?.ToString() ?? "$null";
                result[key] = DumpInternal(entry.Value, depth - 1, visited);
            }
            return result;
        }

        // IEnumerable (but not string/byte[]) — dump as array
        if (obj is IEnumerable enumerable)
        {
            var list = new List<object>();
            int count = 0;
            foreach (var item in enumerable)
            {
                if (count++ >= MaxCollectionItems) break;
                list.Add(DumpInternal(item, depth - 1, visited));
            }
            return list;
        }

        // Complex object — dump all public instance properties
        var props = new Dictionary<string, object>();
        props["$type"] = type.Name;

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // Skip indexers
            if (prop.GetIndexParameters().Length > 0)
                continue;

            // Skip delegate properties (event handlers)
            if (typeof(Delegate).IsAssignableFrom(prop.PropertyType))
                continue;

            // Skip properties from UI/threading namespaces
            if (prop.PropertyType.Namespace != null &&
                SkipNamespaces.Contains(prop.PropertyType.Namespace))
                continue;

            try
            {
                var val = prop.GetValue(obj);
                props[prop.Name] = DumpInternal(val, depth - 1, visited);
            }
            catch (Exception ex)
            {
                props[prop.Name] = "$error: " + (ex.InnerException?.Message ?? ex.Message);
            }
        }
        return props;
    }
}

/// <summary>
/// Reference equality comparer for circular reference detection.
/// </summary>
class ReferenceEqualityComparer : IEqualityComparer<object>
{
    public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

    public new bool Equals(object x, object y) => ReferenceEquals(x, y);
    public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}
