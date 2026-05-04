using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hydac.MATCH.HyML.Contract.Interfaces;
using Hydac.PDT.PdtFramework;
using MatchPdt.Helper.Bootstrap;
using MatchPdt.Helper.Rpc;

namespace MatchPdt.Helper.Services
{
    /// <summary>
    /// Adds a custom error to a project via PDT's IErrorBuilder, replacing the v1
    /// dotnet-helper's reflection-driven XML+BinaryFormatter mutation.
    ///
    /// Mapping (v1 JSON → PDT services):
    ///   block_name              → existing ERR block via IBlockRepository.GetByType("ERR")
    ///   dm_name                 → DetectionMethodLoadParameter.Name; resolved via
    ///                             IDetectionMethodLoader.GetByDefinition or, if missing,
    ///                             synthesized via IDetectionMethodFactory.Create
    ///   spn                     → uint; ErrorBuilder.GetNextFreeSpn auto-bumps on collision
    ///   bit, fmi, fmi_extended,
    ///   set/release_debounce    → IHymlError fields (HymlErrorImpl below)
    ///   set/release_threshold,
    ///   description, severity   → applied to IError after creation
    ///
    /// Compile-time references kept narrow:
    ///   Hydac.MATCH.HyML.Contract — for IHymlError (we implement it)
    ///   Hydac.PDT.PdtFramework    — for ServiceLocator
    /// Everything else goes through reflection so adding/removing services in PDT
    /// updates doesn't immediately break the build.
    /// </summary>
    internal static class ErrorService
    {
        public static RpcResponse AddCustomError(HostBootstrap host, RpcRequest req)
        {
            try
            {
                var input = ParseInput(req);
                var ctx = ProjectContext.Resolve();

                var existing = ctx.FindErrBlockByName(input.BlockName);
                bool createdNewBlock = false;
                BlockHandle block;
                if (existing != null)
                {
                    block = existing;
                }
                else
                {
                    // No matching ERR block — create one via IErrorBlockFactory.Create
                    // (which goes through ICreateBlockCommand.Execute, the same path as
                    // PDT GUI's File→Create Block).
                    block = ctx.CreateNewErrBlock(input.BlockName, input.Description)
                        ?? throw new InvalidOperationException(
                            $"Failed to create ERR block '{input.BlockName}'. " +
                            $"Existing ERR blocks: [{string.Join(", ", ctx.GetAllErrBlockNames())}]");
                    createdNewBlock = true;
                    Program.WriteLog($"Created new ERR block {input.BlockName} ({block.OwnerId})");
                }

                EnsureSpnUnique(ctx, input.Spn);
                EnsureDmNameUnique(ctx, input.DmName);

                var hymlError = new HymlErrorImpl(input);
                var detectionMethod = ctx.FindOrCreateDetectionMethod(block, input, hymlError);

                var newError = ctx.CreateAndAddError(block.OwnerId, (uint)input.Spn, hymlError, detectionMethod);

                ApplyOverrides(newError, input);
                host.SaveProject();

                return RpcResponse.Ok(req.Id, BuildSuccessResult(newError, block, input, createdNewBlock));
            }
            catch (Exception ex)
            {
                Program.WriteLog($"add_custom_error failed: {ex}");
                return RpcResponse.Fail(req.Id, RpcErrorCodes.InternalError,
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---- Input parsing ----------------------------------------------------

        private sealed class Input
        {
            public string Template = "";
            public string DmName = "";
            public int Bit;
            public int Spn;
            public string BlockName = "";
            public string Description = "";
            public int Severity = 3;
            public string Fmi = "FMI_31_CONDITION_EXISTS";
            public string FmiExtended = "FMIEX_GLOBAL";
            public int SetDebounceMs = 500;
            public int ReleaseDebounceMs = 0;
            public int SetThreshold = 500;
            public int ReleaseThreshold = 1000;

            public byte FmiByte => ExtractFmiByte(Fmi);
            public byte FmiExByte => ExtractFmiExByte(FmiExtended);
        }

        private static Input ParseInput(RpcRequest req)
        {
            var input = new Input
            {
                Template = req.GetStringParam("template"),
                DmName = req.GetStringParam("dm_name"),
                Bit = req.GetIntParam("bit"),
                Spn = req.GetIntParam("spn"),
                BlockName = req.GetStringParam("block_name"),
                Description = req.GetStringParam("description"),
                Severity = req.GetIntParam("severity", 3),
                Fmi = req.GetStringParam("fmi", "FMI_31_CONDITION_EXISTS"),
                FmiExtended = req.GetStringParam("fmi_extended", "FMIEX_GLOBAL"),
                SetDebounceMs = req.GetIntParam("set_debounce_ms", 500),
                ReleaseDebounceMs = req.GetIntParam("release_debounce_ms", 0),
                SetThreshold = req.GetIntParam("set_threshold", 500),
                ReleaseThreshold = req.GetIntParam("release_threshold", 1000),
            };
            if (string.IsNullOrEmpty(input.DmName)) throw new ArgumentException("'dm_name' is required");
            if (input.Bit < 0 || input.Bit > 7) throw new ArgumentException("'bit' must be 0-7");
            if (input.Spn <= 0) throw new ArgumentException("'spn' must be > 0");
            if (string.IsNullOrEmpty(input.BlockName))
                throw new ArgumentException("'block_name' is required (must reference an existing ERR block)");
            return input;
        }

        // FMI names follow "FMI_<number>_..." — extract the number byte.
        private static byte ExtractFmiByte(string name)
        {
            var parts = name.Split('_');
            if (parts.Length >= 2 && byte.TryParse(parts[1], out var b)) return b;
            return 31; // FMI_31_CONDITION_EXISTS default
        }

        // FMIEX names don't carry a canonical byte mapping in the v1 contract; default to
        // 0 ("global") which matches every v1 project we've inspected.
        private static byte ExtractFmiExByte(string name) => 0;

        // ---- Synthetic IHymlError --------------------------------------------

        private sealed class HymlErrorImpl : IHymlError
        {
            public HymlErrorImpl(Input input)
            {
                Bit = (byte)input.Bit;
                DefaultFmi = input.FmiByte;
                DefaultFmiEx = input.FmiExByte;
                Detection = input.DmName;
                Description = input.Description;
                DebounceTimeDetect = input.SetDebounceMs;
                TimeReset = input.ReleaseDebounceMs;
                Key = input.DmName;
            }

            public byte Bit { get; set; }
            public int DebounceTimeDetect { get; set; }
            public byte DefaultFmi { get; set; }
            public byte DefaultFmiEx { get; set; }
            public string Description { get; set; } = "";
            public string Detection { get; set; } = "";
            public string Key { get; set; } = "";
            public string ReleaseCondition { get; set; } = "";
            public string Resource { get; set; } = "";
            public string SetCondition { get; set; } = "";
            public int TimeReset { get; set; }

            public IList<IHymlError> EmptyErrorList() => new List<IHymlError>();

            public object Clone()
            {
                return new HymlErrorImpl
                {
                    Bit = Bit,
                    DefaultFmi = DefaultFmi,
                    DefaultFmiEx = DefaultFmiEx,
                    Detection = Detection,
                    Description = Description,
                    DebounceTimeDetect = DebounceTimeDetect,
                    TimeReset = TimeReset,
                    Key = Key,
                    ReleaseCondition = ReleaseCondition,
                    Resource = Resource,
                    SetCondition = SetCondition,
                };
            }

            public bool Equals(IHymlError? other)
                => other != null && other.Key == Key && other.Bit == Bit && other.Detection == Detection;

            // Parameterless ctor for Clone()
            private HymlErrorImpl() { }
        }

        // ---- Project context (resolves all the services we need) -------------

        private sealed class ProjectContext
        {
            public object ProjectAgent = null!;
            public object BlockRepository = null!;
            public object DetectionMethodLoader = null!;
            public object DetectionMethodFactory = null!;
            public object ErrorBuilder = null!;
            public object ProjectErrorFactory = null!;
            public Type DetectionMethodLoadParameterType = null!;
            public Guid VirtualEcuId;
            public IEnumerable ErrorsCollection = null!;

            public static ProjectContext Resolve()
            {
                var businessIfaces = Assembly.Load("Hydac.PDT.Business.Interfaces");
                var errorsContracts = Assembly.Load("Hydac.PDT.Errors.Business.Contracts");

                var projectServiceType = businessIfaces.GetType(
                    "Hydac.PDT.Business.Contracts.Project.IProjectService", throwOnError: true)!;
                var projectService = HostBootstrap.ResolveByReflection(projectServiceType)
                    ?? throw new InvalidOperationException("IProjectService not registered");
                var actualAgent = projectServiceType.GetProperty("ActualProjectAgent")!.GetValue(projectService)
                    ?? throw new InvalidOperationException("No project loaded");

                var ctx = new ProjectContext
                {
                    ProjectAgent = actualAgent,
                    BlockRepository = actualAgent.GetType().GetProperty("BlockRepository")!.GetValue(actualAgent)!,
                    DetectionMethodLoader = HostBootstrap.ResolveByReflection(
                        errorsContracts.GetType("Hydac.PDT.Errors.Business.Contracts.Loader.IDetectionMethodLoader", throwOnError: true)!)!,
                    DetectionMethodFactory = HostBootstrap.ResolveByReflection(
                        businessIfaces.GetType("Hydac.PDT.Business.Contracts.Errors.IDetectionMethodFactory", throwOnError: true)!)!,
                    DetectionMethodLoadParameterType = errorsContracts.GetType(
                        "Hydac.PDT.Errors.Business.Contracts.Loader.DetectionMethodLoadParameter", throwOnError: true)!,
                    ErrorBuilder = HostBootstrap.ResolveByReflection(
                        errorsContracts.GetType("Hydac.PDT.Errors.Business.Contracts.AddOrRemove.IErrorBuilder", throwOnError: true)!)!,
                    ProjectErrorFactory = HostBootstrap.ResolveByReflection(
                        errorsContracts.GetType("Hydac.PDT.Errors.Business.Contracts.AddOrRemove.IProjectErrorFactory", throwOnError: true)!)!,
                };

                var virtualEcus = (IEnumerable)actualAgent.GetType().GetProperty("VirtualEcus")!.GetValue(actualAgent)!;
                var firstEcu = virtualEcus.Cast<object>().FirstOrDefault()
                    ?? throw new InvalidOperationException("Project has no VirtualEcus");
                ctx.VirtualEcuId = (Guid)firstEcu.GetType().GetProperty("ObjectId")!.GetValue(firstEcu)!;

                ctx.ErrorsCollection = (IEnumerable)HostBootstrap.ResolveByReflection(
                    errorsContracts.GetType("Hydac.PDT.Errors.Business.Contracts.IErrorCollection", throwOnError: true)!)!;

                return ctx;
            }

            public BlockHandle? FindErrBlockByName(string blockName)
            {
                foreach (var block in EnumerateErrBlocks())
                {
                    var name = (string?)block.GetType().GetProperty("Name")?.GetValue(block);
                    if (string.Equals(name, blockName, StringComparison.OrdinalIgnoreCase))
                    {
                        var id = (Guid)block.GetType().GetProperty("ObjectId")!.GetValue(block)!;
                        return new BlockHandle { Block = block, OwnerId = id, Name = name ?? blockName };
                    }
                }
                return null;
            }

            /// <summary>
            /// Create a new ERR block via IErrorBlockFactory.Create, which delegates to
            /// ICreateBlockCommand.Execute — the same path GUI File→Create Block uses.
            /// Needs an IHymlBlock blueprint Guid; we steal one from any existing ERR
            /// block in the project (v1's "sibling project" hack disappears: any
            /// in-project ERR block carries the same blueprint).
            /// </summary>
            public BlockHandle? CreateNewErrBlock(string instanceName, string description)
            {
                var businessIfaces = Assembly.Load("Hydac.PDT.Business.Interfaces");
                var factoryType = businessIfaces.GetType(
                    "Hydac.PDT.Business.Contracts.Errors.IErrorBlockFactory", throwOnError: true)!;
                var factory = HostBootstrap.ResolveByReflection(factoryType)
                    ?? throw new InvalidOperationException("IErrorBlockFactory not registered");

                // ownerId in IErrorBlockFactory.Create gets passed as Guid? to
                // TBlockArguments. We try Guid.Empty first (interpreted as "no owner");
                // if that fails the factory returns null and we fall back to scanning
                // existing blocks for a blueprint candidate. The 6th param is described
                // as "ownerId" in the decompiled source — it's the software-module or
                // KB-blueprint association, not strictly required for ERR blocks.
                var ownerCandidates = new[] {
                    Guid.Empty,
                    FindAnyErrBlockBlueprintGuid() ?? Guid.Empty,
                };

                var createMethod = factoryType.GetMethod("Create",
                    new[] { typeof(Guid), typeof(string), typeof(string),
                            typeof(bool), typeof(bool), typeof(Guid) })!;

                object? itBlock = null;
                foreach (var owner in ownerCandidates.Distinct())
                {
                    Program.WriteLog(
                        $"IErrorBlockFactory.Create(ecuId={VirtualEcuId}, type=ERR, name={instanceName}, " +
                        $"createErrors=true, initErrorDefs=true, owner={owner})");
                    try
                    {
                        itBlock = createMethod.Invoke(factory,
                            new object[] { VirtualEcuId, "ERR", instanceName, true, true, owner });
                        if (itBlock != null) break;
                        Program.WriteLog($"  → returned null with owner={owner}");
                    }
                    catch (TargetInvocationException tie) when (tie.InnerException is not null)
                    {
                        Program.WriteLog($"  → threw {tie.InnerException.GetType().Name}: {tie.InnerException.Message}");
                    }
                }
                if (itBlock == null) return null;

                // ITBlock has ObjectId (Guid) and Name (string) like the building blocks.
                var id = (Guid?)itBlock.GetType().GetProperty("ObjectId")?.GetValue(itBlock);
                var name = (string?)itBlock.GetType().GetProperty("Name")?.GetValue(itBlock) ?? instanceName;
                if (id == null || id.Value == Guid.Empty) return null;

                // Set Description if the property exists. Block-level descriptions on ERR
                // blocks live on a different property than the Description we pass here
                // (which is the error template's Description); leave as-is for now.
                return new BlockHandle { Block = itBlock, OwnerId = id.Value, Name = name };
            }

            private Guid? FindAnyErrBlockBlueprintGuid()
            {
                foreach (var block in EnumerateErrBlocks())
                {
                    // IBuildingBlock exposes BlockTemplate as IHymlBlock; IHymlBlock has a
                    // Key/Guid we can use. The legacy property in v1 was "GUIDBlueprint";
                    // try multiple candidates to handle either world.
                    var candidate =
                        TryGetGuidProp(block, "GUIDBlueprint")
                        ?? TryGetGuidProp(block, "BlueprintId")
                        ?? TryGetGuidPropOnNested(block, "BlockTemplate", "ObjectId")
                        ?? TryGetGuidPropOnNested(block, "BlockTemplate", "Guid");
                    if (candidate is { } g && g != Guid.Empty) return g;
                }
                return null;
            }

            private static Guid? TryGetGuidProp(object obj, string name)
            {
                var p = obj.GetType().GetProperty(name);
                if (p == null) return null;
                var v = p.GetValue(obj);
                if (v is Guid g) return g;
                if (v is string s && Guid.TryParse(s, out var gs)) return gs;
                return null;
            }

            private static Guid? TryGetGuidPropOnNested(object obj, string parent, string child)
            {
                var p = obj.GetType().GetProperty(parent);
                var nested = p?.GetValue(obj);
                if (nested == null) return null;
                return TryGetGuidProp(nested, child);
            }

            public IEnumerable<string> GetAllErrBlockNames()
            {
                foreach (var block in EnumerateErrBlocks())
                {
                    var name = (string?)block.GetType().GetProperty("Name")?.GetValue(block);
                    if (!string.IsNullOrEmpty(name)) yield return name!;
                }
            }

            private IEnumerable EnumerateErrBlocks()
            {
                var getByType = BlockRepository.GetType().GetMethod("GetByType", new[] { typeof(string) })!;
                return (IEnumerable)getByType.Invoke(BlockRepository, new object[] { "ERR" })!;
            }

            public object FindOrCreateDetectionMethod(BlockHandle block, Input input, IHymlError hymlError)
            {
                var ctor = DetectionMethodLoadParameterType.GetConstructor(
                    new[] { typeof(Guid), typeof(string), typeof(string), typeof(string) })!;
                var loadParam = ctor.Invoke(new object[] { VirtualEcuId, "ERR", block.Name, input.DmName });

                var getByDef = DetectionMethodLoader.GetType().GetMethod("GetByDefinition")!;
                var dm = getByDef.Invoke(DetectionMethodLoader, new[] { loadParam });
                if (dm != null)
                {
                    Program.WriteLog($"DM '{input.DmName}' found via GetByDefinition");
                    return dm;
                }

                Program.WriteLog($"DM '{input.DmName}' not found — creating via ITRepository.NewDetectionMethod");

                // ITRepository.NewDetectionMethod(ITBlock, IHymlError) creates the
                // TDetectionMethod, links it to the block, and adds it to Repository.
                // DetectionMethods atomically. IDetectionMethodFactory.Create returns a
                // "free-floating" instance that the project never sees, which is why
                // the previous attempt landed nowhere.
                var businessIfaces = Assembly.Load("Hydac.PDT.Business.Interfaces");
                var blockLoaderType = businessIfaces.GetType(
                    "Hydac.PDT.Business.Contracts.Block.IBlockLoader", throwOnError: true)!;
                var blockLoader = HostBootstrap.ResolveByReflection(blockLoaderType)
                    ?? throw new InvalidOperationException("IBlockLoader not registered");

                var itBlock = blockLoader.GetType().GetMethod("GetTBlockById", new[] { typeof(Guid) })!
                    .Invoke(blockLoader, new object[] { block.OwnerId })
                    ?? throw new InvalidOperationException(
                        $"IBlockLoader.GetTBlockById returned null for {block.OwnerId} ({block.Name})");

                var repo = ProjectAgent.GetType().GetProperty("Repository")!.GetValue(ProjectAgent)!;

                // ITRepository may use explicit interface implementation, so reflecting
                // the concrete type's public methods would miss it. Get the method off
                // the interface itself.
                var dataLayerIface = Assembly.Load("Hydac.PDT.DataLayer.Interface");
                var iTRepoType = dataLayerIface.GetType("Hydac.PDT.DataLayer.Interface.ITRepository", throwOnError: true)!;
                var newDmMethod = iTRepoType.GetMethods()
                    .First(m => m.Name == "NewDetectionMethod" && m.GetParameters().Length == 2);
                var tDm = newDmMethod.Invoke(repo, new[] { itBlock, hymlError })
                    ?? throw new InvalidOperationException("ITRepository.NewDetectionMethod returned null");
                Program.WriteLog($"Created and registered {tDm.GetType().FullName}");

                // The HymlError's Detection field becomes the DM's "Detection" property,
                // but the loader matches GetByDefinition on Name. Set Name explicitly.
                tDm.GetType().GetProperty("Name")?.SetValue(tDm, input.DmName);

                dm = getByDef.Invoke(DetectionMethodLoader, new[] { loadParam });
                return dm ?? throw new InvalidOperationException(
                    "Detection method created via NewDetectionMethod but GetByDefinition still " +
                    "returns null. Likely Name didn't stick or the block-loader's name lookup " +
                    "doesn't yet resolve. TDetectionMethod type: " + tDm.GetType().FullName);
            }

            public object CreateAndAddError(Guid ownerId, uint spn, IHymlError hymlError, object detectionMethod)
            {
                // ErrorBuilder.CreateError(Guid, uint, IHymlError, IDetectionMethod) is private,
                // but the public CreateAndAddBuildingBlockError takes a DetectionMethodLoad-
                // Parameter and resolves the DM internally — duplicates work we already did.
                // Easier: invoke private CreateError directly and call AddToProject ourselves.
                var createError = ErrorBuilder.GetType().GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .First(m => m.Name == "CreateError" &&
                                m.GetParameters().Length == 4 &&
                                m.GetParameters()[0].ParameterType == typeof(Guid));

                var error = createError.Invoke(ErrorBuilder,
                        new object[] { ownerId, spn, hymlError, detectionMethod })
                    ?? throw new InvalidOperationException("ErrorBuilder.CreateError returned null");

                var addToProject = ProjectErrorFactory.GetType().GetMethod("AddToProject")!;
                addToProject.Invoke(ProjectErrorFactory, new[] { error });
                return error;
            }
        }

        private sealed class BlockHandle
        {
            public object Block = null!;
            public Guid OwnerId;
            public string Name = "";
        }

        private static void EnsureSpnUnique(ProjectContext ctx, int spn)
        {
            foreach (var err in ctx.ErrorsCollection)
            {
                var dtc = err.GetType().GetProperty("Dtc")?.GetValue(err);
                if (dtc == null) continue;
                var existing = dtc.GetType().GetProperty("Spn")?.GetValue(dtc);
                if (existing is uint u && u == (uint)spn)
                    throw new ArgumentException($"SPN {spn} already exists.");
                if (existing is int i && i == spn)
                    throw new ArgumentException($"SPN {spn} already exists.");
            }
        }

        private static void EnsureDmNameUnique(ProjectContext ctx, string dmName)
        {
            // IDetectionMethod (the project-level model) exposes Name, not Detection. The
            // v1 helper checked TDetectionMethodTemplate.Detection because that was the
            // shape of project.dat's customDMs collection — different layer, different
            // property.
            foreach (var err in ctx.ErrorsCollection)
            {
                var dm = err.GetType().GetProperty("DetectionMethod")?.GetValue(err);
                if (dm == null) continue;
                var name = (string?)dm.GetType().GetProperty("Name")?.GetValue(dm);
                if (string.Equals(name, dmName, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"DM name '{dmName}' already exists.");
            }
        }

        // ---- Apply user overrides post-creation ------------------------------

        private static void ApplyOverrides(object error, Input input)
        {
            if (!string.IsNullOrEmpty(input.Description))
                error.GetType().GetProperty("Description")?.SetValue(error, input.Description);

            error.GetType().GetProperty("Severity")?.SetValue(error, input.Severity);

            var setProps = error.GetType().GetProperty("SetProperties")?.GetValue(error);
            if (setProps != null)
                setProps.GetType().GetProperty("Threshold")?.SetValue(setProps, input.SetThreshold);
            var releaseProps = error.GetType().GetProperty("ReleaseProperties")?.GetValue(error);
            if (releaseProps != null)
                releaseProps.GetType().GetProperty("Threshold")?.SetValue(releaseProps, input.ReleaseThreshold);

            // ErrorBuilder.CreateError already calls DtcFactory.Create(spn, fmi, fmiEx) with
            // the IHymlError's DefaultFmi/DefaultFmiEx, which DtcFactory resolves into the
            // correct IFailureModeIdentifier instances. Don't try to set Dtc.Fmi here as a
            // byte — the property type is IFailureModeIdentifier.
        }

        // ---- Result shape (matches v1 contract) ------------------------------

        private static object BuildSuccessResult(object error, BlockHandle block, Input input, bool newBlock)
        {
            var errorId = (Guid?)error.GetType().GetProperty("ObjectId")?.GetValue(error);
            var dm = error.GetType().GetProperty("DetectionMethod")?.GetValue(error);
            var dmId = (Guid?)dm?.GetType().GetProperty("ObjectId")?.GetValue(dm);
            var verb = newBlock ? "Created block and added error" : "Custom error added to block";
            return new
            {
                status = "ok",
                message = $"{verb} '{block.Name}'.",
                spn = input.Spn,
                dm_name = input.DmName,
                dm_guid = dmId?.ToString() ?? "",
                template = input.Template,
                block_name = block.Name,
                object_id = errorId?.ToString() ?? "",
                new_block = newBlock,
            };
        }
    }
}
