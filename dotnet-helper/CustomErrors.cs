using System;
using System.Collections;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

#pragma warning disable SYSLIB0011 // BinaryFormatter is obsolete
#pragma warning disable CS8632     // Nullable annotations outside #nullable context

/// <summary>
/// Custom error management on top of the XML-direct (v1) path:
///   err-list-custom      read   — every error whose DetectionMethod resolves to a
///                                 renamed slot of an ERR block, joined with block,
///                                 bit, template and FMI names.
///   err-custom-update    write  — change error fields and/or rename its DM.
///   err-custom-delete    write  — remove the error, free its block slot, drop the
///                                 template + customDM entries for that DM.
///   err-block-delete     write  — remove an ERR block, its errors, its template.
///
/// These mirror what CustomErrorAdd registers, in reverse. A "custom" error is one
/// whose DetectionMethod GUID is the ObjectId of a TDetectionMethod slot on an
/// ERR-type TBlock and whose slot CustomName differs from the slot Name
/// (DM_ERR_NN) — that is exactly how PDT itself tells a used slot from a free one.
/// </summary>
partial class Program
{
    sealed class ErrCtx
    {
        public object Repo;
        public IList TBlocks;
        public IList Errors;
        public IList RepoDMs;
        public IList Fmis;
        public IList FmiExts;
        public IList CustomDMs;
        public IList CustomTemplates;
        public IList RepoBlocks;
        public IList RepoSections;
        public IList RepoParams;
    }

    sealed class CustomErrorRef
    {
        public object Error;
        public object Bdm;
        public object Block;
        public object Template;     // may be null
        public int Bit;
        public string DmName = "";
        public string BlockName = "";
        public string BlockId = "";
        public string TemplateName = "";
    }

    static ErrCtx LoadErrCtx(object projectRoot, object errRoot)
    {
        var repo = GetProp(projectRoot, "Repo") ?? throw new InvalidOperationException("Repo not found");
        var repoProject = GetProp(repo, "RepoProject");
        var detail = repoProject != null ? GetProp(repoProject, "Detail") : null;
        var dmData = detail != null ? GetProp(detail, "DetectionMethodData") : null;
        var customSet = dmData != null ? GetProp(dmData, "Custom") : null;
        var revSofts = GetProp(repo, "RevisionSoftwares") as IList;
        var ecu = revSofts != null && revSofts.Count > 0 ? GetProp(revSofts[0], "Ecu") : null;
        return new ErrCtx
        {
            Repo = repo,
            TBlocks = (ecu != null ? GetProp(ecu, "TBlocks") as IList : null) ?? new List<object>(),
            Errors = (GetProp(errRoot, "Errors") as IList) ?? throw new InvalidOperationException("Errors.dat: Errors not found"),
            RepoDMs = GetProp(repo, "DetectionMethod") as IList,
            Fmis = GetProp(repo, "Fmis") as IList,
            FmiExts = GetProp(repo, "FmiExts") as IList,
            CustomDMs = customSet != null ? GetProp(customSet, "DetectionMethods") as IList : null,
            CustomTemplates = customSet != null ? GetProp(customSet, "ErrorTemplates") as IList : null,
            RepoBlocks = GetProp(repo, "Blocks") as IList,
            RepoSections = GetProp(repo, "BlockParamSections") as IList,
            RepoParams = GetProp(repo, "BlockParams") as IList,
        };
    }

    static bool IsReal(object o) => o != null && !(o is string);

    static IEnumerable<object> ErrBlocks(ErrCtx c)
    {
        foreach (var b in c.TBlocks)
            if (IsReal(b) && (GetPropValue<string>(b, "Type") ?? "") == "ERR")
                yield return b;
    }

    /// <summary>Map slot ObjectId → (block, slot) over every ERR block.</summary>
    static Dictionary<string, (object block, object bdm)> SlotIndex(ErrCtx c)
    {
        var idx = new Dictionary<string, (object, object)>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in ErrBlocks(c))
        {
            var slots = GetProp(block, "TDetectionMethods") as IList;
            if (slots == null) continue;
            foreach (var slot in slots)
            {
                if (!IsReal(slot)) continue;
                var id = GetPropValue<string>(slot, "ObjectId") ?? "";
                if (!string.IsNullOrEmpty(id)) idx[id] = (block, slot);
            }
        }
        return idx;
    }

    static bool SlotIsUsed(object slot)
    {
        var name = GetPropValue<string>(slot, "Name") ?? "";
        var custom = GetPropValue<string>(slot, "CustomName") ?? "";
        return !string.IsNullOrEmpty(custom) && !custom.Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    static object FindTemplateForBlock(ErrCtx c, string blockId)
    {
        if (c.CustomTemplates == null) return null;
        foreach (var t in c.CustomTemplates)
        {
            if (!IsReal(t)) continue;
            var linked = GetProp(t, "LinkedBlockIds") as IList;
            if (linked == null) continue;
            foreach (var id in linked)
                if ((id?.ToString() ?? "").Equals(blockId, StringComparison.OrdinalIgnoreCase))
                    return t;
        }
        return null;
    }

    static string NameOfGuid(IList list, string guid)
    {
        if (list == null || string.IsNullOrEmpty(guid)) return "";
        foreach (var o in list)
        {
            if (!IsReal(o)) continue;
            var id = GetPropValue<string>(o, "ObjectId") ?? GetPropValue<string>(o, "GUID") ?? "";
            if (id.Equals(guid, StringComparison.OrdinalIgnoreCase))
                return GetPropValue<string>(o, "Name") ?? "";
        }
        return "";
    }

    static CustomErrorRef Describe(ErrCtx c, object error, Dictionary<string, (object block, object bdm)> slots)
    {
        var dmId = GetPropValue<string>(error, "DetectionMethod") ?? "";
        if (string.IsNullOrEmpty(dmId) || !slots.TryGetValue(dmId, out var hit)) return null;
        if (!SlotIsUsed(hit.bdm)) return null;
        var blockId = GetPropValue<string>(hit.block, "ObjectId") ?? "";
        var tmpl = FindTemplateForBlock(c, blockId);
        return new CustomErrorRef
        {
            Error = error,
            Bdm = hit.bdm,
            Block = hit.block,
            Template = tmpl,
            Bit = GetPropValue<int>(hit.bdm, "Bit"),
            DmName = GetPropValue<string>(hit.bdm, "CustomName") ?? "",
            BlockName = GetPropValue<string>(hit.block, "Name") ?? "",
            BlockId = blockId,
            TemplateName = tmpl != null ? (GetPropValue<string>(tmpl, "Type") ?? "") : "",
        };
    }

    static CustomErrorRef ResolveCustomError(ErrCtx c, int spn)
    {
        object error = null;
        foreach (var e in c.Errors)
            if (IsReal(e) && GetPropValue<int>(e, "Spn") == spn) { error = e; break; }
        if (error == null) throw new ArgumentException($"No error with SPN {spn}.");
        var r = Describe(c, error, SlotIndex(c));
        if (r == null)
            throw new ArgumentException(
                $"SPN {spn} is not a custom error (its detection method is not a used slot of an ERR block); refusing to touch it.");
        return r;
    }

    static object ErrorSummary(ErrCtx c, CustomErrorRef r)
    {
        var e = r.Error;
        var setProps = GetProp(e, "SetProperties");
        var releaseProps = GetProp(e, "ReleaseProperties");
        return new
        {
            spn = GetPropValue<int>(e, "Spn"),
            description = GetPropValue<string>(e, "Description") ?? "",
            severity = GetPropValue<int>(e, "Severity"),
            comment = GetPropValue<string>(e, "Comment") ?? "",
            dm_name = r.DmName,
            dm_guid = GetPropValue<string>(r.Bdm, "ObjectId") ?? "",
            bit = r.Bit,
            block_name = r.BlockName,
            block_guid = r.BlockId,
            template = r.TemplateName,
            fmi = NameOfGuid(c.Fmis, GetPropValue<string>(e, "Fmi") ?? ""),
            fmi_extended = NameOfGuid(c.FmiExts, GetPropValue<string>(e, "FmiExtended") ?? ""),
            set_debounce_ms = setProps != null ? GetPropValue<int>(setProps, "DebounceTime") : 0,
            set_threshold = setProps != null ? GetPropValue<int>(setProps, "Threshold") : 0,
            release_debounce_ms = releaseProps != null ? GetPropValue<int>(releaseProps, "DebounceTime") : 0,
            release_threshold = releaseProps != null ? GetPropValue<int>(releaseProps, "Threshold") : 0,
            error_guid = GetPropValue<string>(e, "ObjectId") ?? "",
        };
    }

    // -----------------------------------------------------------------------
    // err-list-custom
    // -----------------------------------------------------------------------

    static string ErrListCustom(ZipArchive zip)
    {
        var c = LoadErrCtx(Deserialize(zip, "project.dat"), Deserialize(zip, "Errors.dat"));
        var slots = SlotIndex(c);
        var result = new List<object>();
        foreach (var e in c.Errors)
        {
            if (!IsReal(e)) continue;
            var r = Describe(c, e, slots);
            if (r != null) result.Add(ErrorSummary(c, r));
        }
        // Free slots per block, so the caller knows where the next error can go.
        var blocks = new List<object>();
        foreach (var block in ErrBlocks(c))
        {
            var used = new List<object>();
            var free = new List<int>();
            var slotList = GetProp(block, "TDetectionMethods") as IList;
            if (slotList != null)
                foreach (var s in slotList)
                {
                    if (!IsReal(s)) continue;
                    if (SlotIsUsed(s)) used.Add(new { bit = GetPropValue<int>(s, "Bit"), dm_name = GetPropValue<string>(s, "CustomName") ?? "" });
                    else free.Add(GetPropValue<int>(s, "Bit"));
                }
            var blockId = GetPropValue<string>(block, "ObjectId") ?? "";
            var tmpl = FindTemplateForBlock(c, blockId);
            blocks.Add(new
            {
                block_name = GetPropValue<string>(block, "Name") ?? "",
                block_guid = blockId,
                template = tmpl != null ? (GetPropValue<string>(tmpl, "Type") ?? "") : "",
                used_bits = used,
                free_bits = free,
            });
        }
        return JsonSerializer.Serialize(new { errors = result, blocks }, new JsonSerializerOptions { WriteIndented = true });
    }

    // -----------------------------------------------------------------------
    // err-custom-update
    // -----------------------------------------------------------------------

    static string CustomErrorUpdate(string hdbPath, string stdinJson)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(stdinJson);
        int spn = payload.GetProperty("spn").GetInt32();
        string resultJson = "";

        ModifyHdb(hdbPath, new[] { "project.dat", "Errors.dat" }, (roots, rawBytes) =>
        {
            var c = LoadErrCtx(roots["project.dat"], roots["Errors.dat"]);
            var r = ResolveCustomError(c, spn);
            var e = r.Error;
            var changed = new List<string>();

            if (TryStr(payload, "description", out var description))
            {
                SetProp(e, "Description", description);
                changed.Add("description");
            }
            if (TryStr(payload, "comment", out var comment))
            {
                SetProp(e, "Comment", comment);
                changed.Add("comment");
            }
            if (TryInt(payload, "severity", out var severity))
            {
                SetProp(e, "Severity", severity);
                changed.Add("severity");
            }
            if (TryStr(payload, "fmi", out var fmiName))
            {
                var guid = ResolveFmiGuid(c.Fmis, fmiName);
                if (string.IsNullOrEmpty(guid)) throw new ArgumentException($"FMI '{fmiName}' not found. Use list_fmi_definitions.");
                SetProp(e, "Fmi", guid);
                changed.Add("fmi");
            }
            if (TryStr(payload, "fmi_extended", out var fmiExtName))
            {
                var guid = ResolveFmiExtGuid(c.FmiExts, fmiExtName);
                if (string.IsNullOrEmpty(guid)) throw new ArgumentException($"FMI extension '{fmiExtName}' not found. Use list_fmi_definitions.");
                SetProp(e, "FmiExtended", guid);
                changed.Add("fmi_extended");
            }
            var setProps = GetProp(e, "SetProperties");
            var releaseProps = GetProp(e, "ReleaseProperties");
            if (TryInt(payload, "set_debounce_ms", out var sdm) && setProps != null)
            {
                SetProp(setProps, "DebounceTime", sdm);
                SetProp(setProps, "IsDebounceEnabled", sdm > 0);
                changed.Add("set_debounce_ms");
            }
            if (TryInt(payload, "set_threshold", out var sth) && setProps != null)
            {
                SetProp(setProps, "Threshold", sth);
                changed.Add("set_threshold");
            }
            if (TryInt(payload, "release_debounce_ms", out var rdm) && releaseProps != null)
            {
                SetProp(releaseProps, "DebounceTime", rdm);
                SetProp(releaseProps, "IsDebounceEnabled", rdm > 0);
                changed.Add("release_debounce_ms");
            }
            if (TryInt(payload, "release_threshold", out var rth) && releaseProps != null)
            {
                SetProp(releaseProps, "Threshold", rth);
                changed.Add("release_threshold");
            }
            if (TryInt(payload, "new_spn", out var newSpn) && newSpn != spn)
            {
                foreach (var other in c.Errors)
                    if (IsReal(other) && GetPropValue<int>(other, "Spn") == newSpn)
                        throw new ArgumentException($"SPN {newSpn} already exists.");
                SetProp(e, "Spn", newSpn);
                changed.Add("spn");
            }
            if (TryStr(payload, "dm_name", out var newDm) && !newDm.Equals(r.DmName, StringComparison.Ordinal))
            {
                EnsureDmNameFree(c, newDm, r.DmName);
                RenameDm(c, r, newDm);
                changed.Add("dm_name");
            }

            if (changed.Count == 0)
                throw new ArgumentException("Nothing to update: provide at least one field.");

            resultJson = JsonSerializer.Serialize(new
            {
                status = "ok",
                changed,
                error = ErrorSummary(c, r),
                message = $"Updated custom error SPN {spn} ({string.Join(", ", changed)}).",
            }, new JsonSerializerOptions { WriteIndented = true });
        });

        return resultJson;
    }

    static void EnsureDmNameFree(ErrCtx c, string dmName, string allowExisting)
    {
        foreach (var block in ErrBlocks(c))
        {
            var slots = GetProp(block, "TDetectionMethods") as IList;
            if (slots == null) continue;
            foreach (var s in slots)
            {
                if (!IsReal(s)) continue;
                var n = GetPropValue<string>(s, "CustomName") ?? "";
                if (n.Equals(dmName, StringComparison.OrdinalIgnoreCase) && !n.Equals(allowExisting, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"DM name '{dmName}' already used in block '{GetPropValue<string>(block, "Name")}'.");
            }
        }
        if (c.CustomDMs != null)
            foreach (var dm in c.CustomDMs)
            {
                if (!IsReal(dm)) continue;
                var n = GetPropValue<string>(dm, "Detection") ?? "";
                if (n.Equals(dmName, StringComparison.OrdinalIgnoreCase) && !n.Equals(allowExisting, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"DM name '{dmName}' already exists.");
            }
    }

    /// <summary>Rename a DM everywhere CustomErrorAdd wrote it.</summary>
    static void RenameDm(ErrCtx c, CustomErrorRef r, string newName)
    {
        var oldName = r.DmName;
        SetProp(r.Bdm, "CustomName", newName);
        if (c.CustomDMs != null)
            foreach (var dm in c.CustomDMs)
            {
                if (!IsReal(dm)) continue;
                if ((GetPropValue<string>(dm, "Detection") ?? "").Equals(oldName, StringComparison.OrdinalIgnoreCase))
                {
                    SetProp(dm, "Detection", newName);
                    try { SetProp(dm, "DetectionVm", newName); } catch { }
                }
            }
        if (r.Template != null)
        {
            var entries = GetProp(r.Template, "Error") as IList;
            if (entries != null)
                foreach (var en in entries)
                    if (IsReal(en) && (GetPropValue<string>(en, "Detection") ?? "").Equals(oldName, StringComparison.OrdinalIgnoreCase))
                        SetProp(en, "Detection", newName);
        }
        r.DmName = newName;
    }

    // -----------------------------------------------------------------------
    // err-custom-delete
    // -----------------------------------------------------------------------

    static string CustomErrorDelete(string hdbPath, string stdinJson)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(stdinJson);
        int spn = payload.GetProperty("spn").GetInt32();
        string resultJson = "";

        ModifyHdb(hdbPath, new[] { "project.dat", "Errors.dat" }, (roots, rawBytes) =>
        {
            var c = LoadErrCtx(roots["project.dat"], roots["Errors.dat"]);
            var r = ResolveCustomError(c, spn);
            var summary = ErrorSummary(c, r);

            var removedDmEntries = ReleaseSlot(c, r.Bdm, r.Template);
            c.Errors.Remove(r.Error);
            SyncErrorCount(r.Block);

            resultJson = JsonSerializer.Serialize(new
            {
                status = "ok",
                deleted = summary,
                dm_entries_removed = removedDmEntries,
                message = $"Deleted custom error SPN {spn}; bit {r.Bit} of block '{r.BlockName}' is free again.",
            }, new JsonSerializerOptions { WriteIndented = true });
        });

        return resultJson;
    }

    /// <summary>
    /// Free a block slot: CustomName back to the slot Name (DM_ERR_NN) and drop the
    /// customDM + template entries carrying the custom name. Returns how many
    /// entries were removed.
    /// </summary>
    static int ReleaseSlot(ErrCtx c, object bdm, object template)
    {
        var dmName = GetPropValue<string>(bdm, "CustomName") ?? "";
        var slotName = GetPropValue<string>(bdm, "Name") ?? $"DM_ERR_{GetPropValue<int>(bdm, "Bit"):D2}";
        SetProp(bdm, "CustomName", slotName);
        int removed = 0;
        if (!string.IsNullOrEmpty(dmName))
        {
            removed += RemoveWhere(c.CustomDMs, o => (GetPropValue<string>(o, "Detection") ?? "").Equals(dmName, StringComparison.OrdinalIgnoreCase));
            if (template != null)
                removed += RemoveWhere(GetProp(template, "Error") as IList, o => (GetPropValue<string>(o, "Detection") ?? "").Equals(dmName, StringComparison.OrdinalIgnoreCase));
        }
        return removed;
    }

    static int RemoveWhere(IList list, Func<object, bool> pred)
    {
        if (list == null) return 0;
        var victims = new List<object>();
        foreach (var o in list)
            if (IsReal(o) && pred(o)) victims.Add(o);
        foreach (var v in victims) list.Remove(v);
        return victims.Count;
    }

    // -----------------------------------------------------------------------
    // err-block-delete
    // -----------------------------------------------------------------------

    static string ErrBlockDelete(string hdbPath, string stdinJson)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(stdinJson);
        string blockName = payload.GetProperty("block_name").GetString() ?? "";
        if (string.IsNullOrEmpty(blockName)) throw new ArgumentException("'block_name' is required");
        string resultJson = "";

        ModifyHdb(hdbPath, new[] { "project.dat", "Errors.dat" }, (roots, rawBytes) =>
        {
            var c = LoadErrCtx(roots["project.dat"], roots["Errors.dat"]);
            object block = null;
            foreach (var b in ErrBlocks(c))
                if ((GetPropValue<string>(b, "Name") ?? "").Equals(blockName, StringComparison.OrdinalIgnoreCase)) { block = b; break; }
            if (block == null) throw new ArgumentException($"ERR block '{blockName}' not found.");
            blockName = GetPropValue<string>(block, "Name") ?? blockName;
            var blockId = GetPropValue<string>(block, "ObjectId") ?? "";

            // 1. Errors bound to this block's slots.
            var slotIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var slots = GetProp(block, "TDetectionMethods") as IList;
            var slotObjs = new List<object>();
            if (slots != null)
                foreach (var s in slots)
                    if (IsReal(s)) { slotObjs.Add(s); slotIds.Add(GetPropValue<string>(s, "ObjectId") ?? ""); }
            var removedSpns = new List<int>();
            foreach (var e in c.Errors.Cast<object>().Where(IsReal).ToList())
            {
                if (slotIds.Contains(GetPropValue<string>(e, "DetectionMethod") ?? ""))
                {
                    removedSpns.Add(GetPropValue<int>(e, "Spn"));
                    c.Errors.Remove(e);
                }
            }

            // 2. Template link; drop the template when this was its last block.
            var template = FindTemplateForBlock(c, blockId);
            string templateName = "";
            bool templateRemoved = false;
            if (template != null)
            {
                templateName = GetPropValue<string>(template, "Type") ?? "";
                var linked = GetProp(template, "LinkedBlockIds") as IList;
                if (linked != null)
                {
                    var victims = linked.Cast<object>().Where(id => (id?.ToString() ?? "").Equals(blockId, StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (var v in victims) linked.Remove(v);
                    if (linked.Count == 0)
                    {
                        c.CustomTemplates.Remove(template);
                        templateRemoved = true;
                    }
                }
            }
            // customDM entries named after this block's used slots (and, if the
            // template is gone, everything its Error list still referenced).
            var dmNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in slotObjs)
                if (SlotIsUsed(s)) dmNames.Add(GetPropValue<string>(s, "CustomName") ?? "");
            if (templateRemoved)
            {
                var entries = GetProp(template, "Error") as IList;
                if (entries != null)
                    foreach (var en in entries)
                        if (IsReal(en)) dmNames.Add(GetPropValue<string>(en, "Detection") ?? "");
            }
            else if (template != null)
            {
                RemoveWhere(GetProp(template, "Error") as IList, o => dmNames.Contains(GetPropValue<string>(o, "Detection") ?? ""));
            }
            int dmEntriesRemoved = RemoveWhere(c.CustomDMs, o => dmNames.Contains(GetPropValue<string>(o, "Detection") ?? ""));

            // 3. Repo-level registrations, by identity (the repo lists hold the same
            //    objects the block tree holds).
            int repoDmsRemoved = RemoveWhere(c.RepoDMs, o => slotObjs.Any(s => ReferenceEquals(s, o))
                                                          || (GetPropValue<string>(o, "BlockObjectId") ?? "").Equals(blockId, StringComparison.OrdinalIgnoreCase));
            var sections = new List<object>();
            var prms = new List<object>();
            var blockSections = GetProp(block, "TBlockParamSections") as IList;
            if (blockSections != null)
                foreach (var s in blockSections)
                {
                    if (!IsReal(s)) continue;
                    sections.Add(s);
                    var ps = GetProp(s, "TBlockParams") as IList;
                    if (ps != null) foreach (var p in ps) if (IsReal(p)) prms.Add(p);
                }
            int sectionsRemoved = RemoveWhere(c.RepoSections, o => sections.Any(s => ReferenceEquals(s, o)));
            int paramsRemoved = RemoveWhere(c.RepoParams, o => prms.Any(p => ReferenceEquals(p, o)));
            int blocksRemoved = RemoveWhere(c.RepoBlocks, o => ReferenceEquals(o, block));
            c.TBlocks.Remove(block);

            resultJson = JsonSerializer.Serialize(new
            {
                status = "ok",
                block_name = blockName,
                block_guid = blockId,
                template = templateName,
                template_removed = templateRemoved,
                errors_removed = removedSpns,
                dm_entries_removed = dmEntriesRemoved,
                repo_dms_removed = repoDmsRemoved,
                sections_removed = sectionsRemoved,
                params_removed = paramsRemoved,
                repo_blocks_removed = blocksRemoved,
                message = $"Deleted ERR block '{blockName}' with {removedSpns.Count} error(s)" +
                          (templateRemoved ? $" and template '{templateName}'." : "."),
            }, new JsonSerializerOptions { WriteIndented = true });
        });

        return resultJson;
    }

    // -----------------------------------------------------------------------
    // Block bookkeeping shared with CustomErrorAdd
    // -----------------------------------------------------------------------

    /// <summary>
    /// Make sure every section/param of an ERR block is registered in the repo's
    /// flat lists (Repo.BlockParamSections / Repo.BlockParams). The PDT GUI resolves
    /// block parameters through those lists: a block whose params are only reachable
    /// via its own tree compiles fine but shows empty cells (Block Name, Behavior
    /// "- none -", Error Count). Returns (sectionsAdded, paramsAdded).
    /// </summary>
    static (int sections, int prms) RegisterBlockParams(object repo, object block)
    {
        var repoSections = GetProp(repo, "BlockParamSections") as IList;
        var repoParams = GetProp(repo, "BlockParams") as IList;
        int s = 0, p = 0;
        var sections = GetProp(block, "TBlockParamSections") as IList;
        if (sections == null) return (0, 0);
        foreach (var sec in sections)
        {
            if (!IsReal(sec)) continue;
            if (repoSections != null && !repoSections.Cast<object>().Any(x => ReferenceEquals(x, sec)))
            {
                repoSections.Add(sec);
                s++;
            }
            var prms = GetProp(sec, "TBlockParams") as IList;
            if (prms == null || repoParams == null) continue;
            foreach (var prm in prms)
            {
                if (!IsReal(prm)) continue;
                if (!repoParams.Cast<object>().Any(x => ReferenceEquals(x, prm)))
                {
                    repoParams.Add(prm);
                    p++;
                }
            }
        }
        return (s, p);
    }

    static object FindBlockParam(object block, string paramName)
    {
        var sections = GetProp(block, "TBlockParamSections") as IList;
        if (sections == null) return null;
        foreach (var sec in sections)
        {
            if (!IsReal(sec)) continue;
            var prms = GetProp(sec, "TBlockParams") as IList;
            if (prms == null) continue;
            foreach (var prm in prms)
                if (IsReal(prm) && (GetPropValue<string>(prm, "Name") ?? "") == paramName)
                    return prm;
        }
        return null;
    }

    static void SetBlockParamText(object block, string paramName, string text, int? asInt)
    {
        var prm = FindBlockParam(block, paramName);
        if (prm == null) return;
        SetProp(prm, "ValueText", text);
        if (asInt.HasValue) { try { SetProp(prm, "ValueAsInteger", asInt.Value); } catch { } }
        var vd = GetProp(prm, "ValueData");
        if (vd != null) { try { SetProp(vd, "Value", text); } catch { } }
    }

    /// <summary>ERROR_COUNT = number of used slots, the way the GUI maintains it.</summary>
    static int SyncErrorCount(object block)
    {
        int used = 0;
        var slots = GetProp(block, "TDetectionMethods") as IList;
        if (slots != null)
            foreach (var s in slots)
                if (IsReal(s) && SlotIsUsed(s)) used++;
        SetBlockParamText(block, "ERROR_COUNT", used.ToString(), used);
        return used;
    }

    // -----------------------------------------------------------------------
    // err-blocks-repair
    // -----------------------------------------------------------------------

    /// <summary>
    /// Fix ERR blocks created by earlier versions of add_custom_error: register
    /// their params in the repo lists and recompute ERROR_COUNT. Idempotent.
    /// </summary>
    static string ErrBlocksRepair(string hdbPath)
    {
        string resultJson = "";
        ModifyHdb(hdbPath, new[] { "project.dat", "Errors.dat" }, (roots, rawBytes) =>
        {
            var c = LoadErrCtx(roots["project.dat"], roots["Errors.dat"]);
            var report = new List<object>();
            foreach (var block in ErrBlocks(c))
            {
                var (secs, prms) = RegisterBlockParams(c.Repo, block);
                var before = GetPropValue<string>(FindBlockParam(block, "ERROR_COUNT") ?? new object(), "ValueText") ?? "";
                var count = SyncErrorCount(block);
                report.Add(new
                {
                    block_name = GetPropValue<string>(block, "Name") ?? "",
                    sections_registered = secs,
                    params_registered = prms,
                    error_count_before = before,
                    error_count = count,
                });
            }
            resultJson = JsonSerializer.Serialize(new
            {
                status = "ok",
                blocks = report,
                message = $"Checked {report.Count} ERR block(s).",
            }, new JsonSerializerOptions { WriteIndented = true });
        });
        return resultJson;
    }

    // -----------------------------------------------------------------------
    // JSON helpers
    // -----------------------------------------------------------------------

    static bool TryStr(JsonElement payload, string name, out string value)
    {
        value = "";
        if (payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
        {
            value = v.GetString() ?? "";
            return true;
        }
        return false;
    }

    static bool TryInt(JsonElement payload, string name, out int value)
    {
        value = 0;
        if (!payload.TryGetProperty(name, out var v)) return false;
        if (v.ValueKind == JsonValueKind.Number) { value = v.GetInt32(); return true; }
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out value)) return true;
        return false;
    }
}
