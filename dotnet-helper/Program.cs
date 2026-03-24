using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text.Json;

#pragma warning disable SYSLIB0011 // BinaryFormatter is obsolete

/// <summary>
/// Deserializes .dat files from a HYDAC PDT .hdb archive using BinaryFormatter
/// and the PDT .NET assemblies. Outputs JSON to stdout.
///
/// Usage: HdbDatReader &lt;hdb_path&gt; &lt;pdt_dir&gt; &lt;command&gt;
///   commands: errors, compileconfig, isobus
/// </summary>
class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: HdbDatReader <hdb_path> <pdt_dir> <command>");
            Console.Error.WriteLine("  commands: errors, compileconfig, isobus");
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
