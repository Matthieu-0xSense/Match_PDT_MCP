using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text.Json;

#pragma warning disable SYSLIB0011 // BinaryFormatter is obsolete
#pragma warning disable CS8632     // Nullable annotations outside #nullable context

/// <summary>
/// Deserializes .dat files from a HYDAC PDT .hdb archive using BinaryFormatter
/// and the PDT .NET assemblies. Outputs JSON to stdout.
///
/// Usage: HdbDatReader &lt;hdb_path&gt; &lt;pdt_dir&gt; &lt;command&gt; [args]
///   commands: errors, compileconfig, isobus, dump &lt;file&gt;, dump-all, list-dat
/// </summary>
class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: HdbDatReader <hdb_path> <pdt_dir> <command> [args]");
            Console.Error.WriteLine("  commands: errors, compileconfig, isobus, dump <file>, dump-all, list-dat");
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
            using var zip = ZipFile.OpenRead(hdbPath);
            string json = command switch
            {
                "errors" => ReadErrors(zip),
                "compileconfig" => ReadCompileConfig(zip),
                "isobus" => ReadIsobus(zip),
                "list-dat" => ListDatFiles(zip),
                "dump" => DumpDatFile(zip, args.Length > 3 ? args[3] : throw new ArgumentException("dump requires a filename argument")),
                "dump-all" => DumpAllDatFiles(zip),
                _ => throw new ArgumentException($"Unknown command: {command}")
            };
            Console.Write(json);
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
