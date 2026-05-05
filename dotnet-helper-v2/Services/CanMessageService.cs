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
    /// Phase 3 target: replace v1 dotnet-helper's add_can_message (XML mutation across
    /// CanMessages.xml, CanMessageEcuLinks.xml, CanSignals.xml) with PDT services.
    ///
    /// Service signatures verified via dnSpy (see MIGRATION_PLAN.md Phase 3):
    ///
    ///   CanMessage(Guid id, Guid busId, string name, int canId,
    ///              CanMessageByteOrder byteOrder, CanMessageType messageType,
    ///              int dlc, byte defaultByte, string description, int cycleTime,
    ///              int minimumCycleTime, int startOffsetTime, int timeOut,
    ///              CanMessageSafety safety, IEnumerable&lt;Guid&gt; consumerApplications,
    ///              bool isMuxed)
    ///   ICanMessageRepository.Add(CanMessage)
    ///
    ///   CanMessageEcuLink(Guid messageId, Guid virtualEcuId, CanMessageEcuLinkUsage usage,
    ///                     Guid bufferBlockId, Guid canBlockId)
    ///   ICanMessageEcuLinkRepository.Add(CanMessageEcuLink)
    ///   ICanMessageEcuLinkService.SetNewUsage(messageId, ecuLinkId, usage?)
    ///       — flipping Usage from null to TXC/RXC triggers CSND/CRCV block creation.
    ///       This kills v1's "set Usage in PDT manually" caveat.
    ///
    ///   CanSignal(Guid id, Guid ownerId, int startBit, int sizeBits, string name,
    ///             CanSignalDefinitionLayer, CanEcuApplicationLayer,
    ///             CanServiceToolDefinitionLayer)
    ///   ICanSignalRepository.Add(CanSignal) — verify exact name
    ///
    ///   ISignalService.GetLastFreeStartBit(Guid messageId) — auto-placement helper
    ///       (new feature unlocked by migration).
    ///
    /// v1 input contract (must preserve):
    ///   name, can_id, direction (SendCyclically | SendEventBased | Receive), dlc,
    ///   cycle_time, signals ("name:startbit:sizebits,..."), bus (1-indexed)
    ///
    /// Open implementation questions (resolve before shipping):
    ///   1. bufferBlockId / canBlockId — what existing blocks does the new ECU link
    ///      attach to? v1 used per-bus "send_buffer" / "recv_buffer" / "ecu_id" metadata
    ///      cached at HDB-load time (see _get_bus_info in server.py). The PDT-services
    ///      equivalent is probably IBlockRepository scans or per-ECU buffer accessors.
    ///   2. Save persistence — IProjectAgent.Save likely doesn't write CanMessages.xml /
    ///      CanSignals.xml in headless mode (same shape as Errors.dat in Phase 2). Mirror
    ///      WriteErrorsDat: find the equivalent of ISaveErrorsToDataLayer for CAN data
    ///      (probably ISaveCanMessagesToDataLayer or similar) and invoke after save.
    ///   3. CanSignalDefinitionLayer / CanEcuApplicationLayer / CanServiceToolDefinitionLayer
    ///      are required ctor args for the full CanSignal — find their construction
    ///      surface (factories or value-types).
    ///   4. CanMessageType vs direction — map "SendCyclically" → TXC, "SendEventBased"
    ///      → TXE, "Receive" → RXC (verify the exact enum values).
    ///
    /// Step-by-step probing recipe (uses RpcLoop.probe_type):
    ///   probe_type ICanMessageRepository                Hydac.PDT.Can.Business.Contracts
    ///   probe_type ICanMessageEcuLinkRepository         Hydac.PDT.Can.Business.Contracts
    ///   probe_type ICanMessageEcuLinkService            Hydac.PDT.Can.Business.Contracts
    ///   probe_type IMessageService                      Hydac.PDT.Can.Business.Contracts
    ///   probe_type ISignalRecordService                 Hydac.PDT.Can.Business.Contracts
    ///   (these confirm concrete types and ctor deps; if any returns null, that
    ///    interface isn't bound and the service map needs revisiting.)
    /// </summary>
    internal static class CanMessageService
    {
        /// <summary>
        /// Phase 3 probe: enumerate ICanMessageRepository.GetAll() and report counts +
        /// each message's identity. Used to verify whether ICanMessageCache populates
        /// at bootstrap (it must — otherwise add_can_message + save would silently
        /// drop pre-existing messages because CanMessagePersistenceAdapter writes
        /// CanMessages.xml from the cache, and snapshot-merge skips files already
        /// present in the saved zip).
        ///
        /// Also dumps ICanMessageEcuLinkRepository.GetAll() + ICanSignalRepository.GetAll()
        /// counts so we know all three caches are warm before any Add.
        /// </summary>
        public static RpcResponse DumpCanMessages(HostBootstrap host, RpcRequest req)
        {
            try
            {
                var canContracts = Assembly.Load("Hydac.PDT.Can.Business.Contracts");
                var msgRepoType = canContracts.GetType(
                    "Hydac.PDT.Can.Business.Contracts.Repository.ICanMessageRepository", throwOnError: true)!;
                var linkRepoType = canContracts.GetType(
                    "Hydac.PDT.Can.Business.Contracts.Repository.ICanMessageEcuLinkRepository", throwOnError: true)!;
                var sigRepoType = canContracts.GetType(
                    "Hydac.PDT.Can.Business.Contracts.Repository.ICanSignalRepository", throwOnError: true)!;
                var linkSvcType = canContracts.GetType(
                    "Hydac.PDT.Can.Business.Contracts.Message.ICanMessageEcuLinkService", throwOnError: true)!;
                var msgSvcType = canContracts.GetType(
                    "Hydac.PDT.Can.Business.Contracts.Message.IMessageService", throwOnError: true)!;
                var sigSvcType = canContracts.GetType(
                    "Hydac.PDT.Can.Business.Contracts.Signal.ISignalService", throwOnError: true)!;

                var msgRepo = HostBootstrap.ResolveByReflection(msgRepoType);
                var linkRepo = HostBootstrap.ResolveByReflection(linkRepoType);
                var sigRepo = HostBootstrap.ResolveByReflection(sigRepoType);
                var linkSvc = HostBootstrap.ResolveByReflection(linkSvcType);
                var msgSvc = HostBootstrap.ResolveByReflection(msgSvcType);
                var sigSvc = HostBootstrap.ResolveByReflection(sigSvcType);

                var messages = (msgRepo == null) ? null : ((IEnumerable)msgRepoType.GetMethod("GetAll")!.Invoke(msgRepo, null)!).Cast<object>().ToList();
                var links = (linkRepo == null) ? null : ((IEnumerable)linkRepoType.GetMethod("GetAll")!.Invoke(linkRepo, null)!).Cast<object>().ToList();
                var signals = (sigRepo == null) ? null : ((IEnumerable)sigRepoType.GetMethod("GetAll")!.Invoke(sigRepo, null)!).Cast<object>().ToList();

                static object Summarize(object? m)
                {
                    if (m == null) return new { _null = true };
                    var t = m.GetType();
                    string? str(string p) => t.GetProperty(p)?.GetValue(m)?.ToString();
                    return new
                    {
                        id = str("Id"),
                        bus_id = str("BusId"),
                        name = str("Name"),
                        can_id = str("CanId"),
                        message_type = str("MessageType"),
                        dlc = str("Dlc"),
                        cycle_time = str("CycleTime"),
                        is_muxed = str("IsMuxed"),
                    };
                }

                static object SummarizeLink(object? l)
                {
                    if (l == null) return new { _null = true };
                    var t = l.GetType();
                    string? str(string p) => t.GetProperty(p)?.GetValue(l)?.ToString();
                    return new
                    {
                        message_id = str("CanMessageId"),
                        ecu_id = str("VirtualEcuId"),
                        usage = str("Usage"),
                        buffer_block = str("BufferBlockObjectId"),
                        can_block = str("CanBlockObjectId"),
                    };
                }

                return RpcResponse.Ok(req.Id, new
                {
                    services_resolved = new
                    {
                        ICanMessageRepository = msgRepo?.GetType().FullName,
                        ICanMessageEcuLinkRepository = linkRepo?.GetType().FullName,
                        ICanSignalRepository = sigRepo?.GetType().FullName,
                        ICanMessageEcuLinkService = linkSvc?.GetType().FullName,
                        IMessageService = msgSvc?.GetType().FullName,
                        ISignalService = sigSvc?.GetType().FullName,
                    },
                    counts = new
                    {
                        messages = messages?.Count ?? -1,
                        ecu_links = links?.Count ?? -1,
                        signals = signals?.Count ?? -1,
                    },
                    sample_messages = messages?.Take(5).Select(Summarize).ToList(),
                    sample_links = links?.Take(5).Select(SummarizeLink).ToList(),
                });
            }
            catch (Exception ex)
            {
                return RpcResponse.Fail(req.Id, RpcErrorCodes.InternalError,
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        public static RpcResponse AddCanMessage(HostBootstrap host, RpcRequest req)
        {
            try
            {
                var name = req.GetStringParam("name");
                var direction = req.GetStringParam("direction", "Receive");
                var dlc = req.GetIntParam("dlc", 8);
                var cycleTime = req.GetIntParam("cycle_time", 50);
                var busOneIndexed = req.GetIntParam("bus", 1);
                var description = req.GetStringParam("description");
                if (string.IsNullOrEmpty(name))
                    throw new ArgumentException("'name' is required");

                int canId = ParseCanId(req);

                var canContracts = Assembly.Load("Hydac.PDT.Can.Business.Contracts");
                var businessIfaces = Assembly.Load("Hydac.PDT.Business.Interfaces");

                var canMessageType = canContracts.GetType("Hydac.PDT.Can.Business.Contracts.Model.CanMessage", throwOnError: true)!;
                var msgRepoType = canContracts.GetType("Hydac.PDT.Can.Business.Contracts.Repository.ICanMessageRepository", throwOnError: true)!;
                var linkSvcType = canContracts.GetType("Hydac.PDT.Can.Business.Contracts.Message.ICanMessageEcuLinkService", throwOnError: true)!;
                var msgSvcType = canContracts.GetType("Hydac.PDT.Can.Business.Contracts.Message.IMessageService", throwOnError: true)!;
                var usageEnum = canContracts.GetType("Hydac.PDT.Can.Business.Contracts.Model.CanMessageEcuLinkUsage", throwOnError: true)!;
                // CanMessageType + CanMessageByteOrder live in Business.Interfaces (Hydac.PDT.Business.Can namespace)
                var msgTypeEnum = businessIfaces.GetType("Hydac.PDT.Business.Can.CanMessageType", throwOnError: true)!;

                var msgRepo = HostBootstrap.ResolveByReflection(msgRepoType)
                    ?? throw new InvalidOperationException("ICanMessageRepository not registered");
                var linkSvc = HostBootstrap.ResolveByReflection(linkSvcType)
                    ?? throw new InvalidOperationException("ICanMessageEcuLinkService not registered");
                var msgSvc = HostBootstrap.ResolveByReflection(msgSvcType)
                    ?? throw new InvalidOperationException("IMessageService not registered");

                // Get IProjectAgent → CanBusses (note: spelled with double-s in PDT API)
                var projectServiceType = businessIfaces.GetType("Hydac.PDT.Business.Contracts.Project.IProjectService", throwOnError: true)!;
                var projectService = HostBootstrap.ResolveByReflection(projectServiceType)
                    ?? throw new InvalidOperationException("IProjectService not registered");
                var actualAgent = projectServiceType.GetProperty("ActualProjectAgent")!.GetValue(projectService)
                    ?? throw new InvalidOperationException("No project loaded");

                var canBusses = (IEnumerable)actualAgent.GetType().GetProperty("CanBusses")!.GetValue(actualAgent)!;
                var busList = canBusses.Cast<object>().ToList();
                if (busOneIndexed < 1 || busOneIndexed > busList.Count)
                    throw new ArgumentException($"'bus' must be 1..{busList.Count}, got {busOneIndexed}");
                var bus = busList[busOneIndexed - 1];
                var busId = (Guid)bus.GetType().GetProperty("ObjectId")!.GetValue(bus)!;

                // First VirtualEcu (matches v1 single-ECU assumption — Phase 3 step 3 minimal scope)
                var virtualEcus = (IEnumerable)actualAgent.GetType().GetProperty("VirtualEcus")!.GetValue(actualAgent)!;
                var firstEcu = virtualEcus.Cast<object>().FirstOrDefault()
                    ?? throw new InvalidOperationException("Project has no VirtualEcus");
                var ecuId = (Guid)firstEcu.GetType().GetProperty("ObjectId")!.GetValue(firstEcu)!;

                // Uniqueness checks against ICanMessageRepository.GetAll()
                var allMessages = ((IEnumerable)msgRepoType.GetMethod("GetAll")!.Invoke(msgRepo, null)!).Cast<object>().ToList();
                foreach (var existing in allMessages)
                {
                    var existingName = (string)existing.GetType().GetProperty("Name")!.GetValue(existing)!;
                    var existingCanId = (int)existing.GetType().GetProperty("CanId")!.GetValue(existing)!;
                    if (string.Equals(existingName, name, StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException($"CAN message name '{name}' already exists.");
                    if (existingCanId == canId)
                        throw new ArgumentException($"CAN ID 0x{canId:X} already used by message '{existingName}'.");
                }

                // Build CanMessage with the convenience ctor (busId, name, canId, isMuxed=false),
                // then chain With() to override Dlc + CycleTime + MessageType + Description.
                var simpleCtor = canMessageType.GetConstructor(new[] { typeof(Guid), typeof(string), typeof(int), typeof(bool) })!;
                var message = simpleCtor.Invoke(new object[] { busId, name, canId, false });

                // Pick CanMessageType: Standard if 11-bit, Extended otherwise.
                var msgType = (canId < 2048) ? Enum.Parse(msgTypeEnum, "Standard") : Enum.Parse(msgTypeEnum, "Extended");

                // .With(... null params for fields we don't change ...) — call With via reflection.
                // Signature: With(Guid id=default, Guid busId=default, string name=null, int? canId=null,
                //                 CanMessageByteOrder? byteOrder=null, CanMessageType? messageType=null,
                //                 int? dlc=null, byte? defaultByte=null, string description=null,
                //                 int? cycleTime=null, int? startOffsetTime=null, int? minimumCycleTime=null,
                //                 int? timeOut=null, CanMessageSafety safety=null,
                //                 IEnumerable<Guid> consumerApplications=null, bool? isMuxed=null)
                // Build args[16] respecting nullable types.
                var withMethod = canMessageType.GetMethod("With")!;
                var withParams = withMethod.GetParameters();
                var withArgs = new object?[withParams.Length];
                for (int i = 0; i < withParams.Length; i++) withArgs[i] = null;
                // index map per signature above:
                withArgs[5] = msgType;        // messageType
                withArgs[6] = (int?)dlc;      // dlc
                withArgs[8] = description;    // description
                withArgs[9] = (int?)cycleTime;// cycleTime
                message = withMethod.Invoke(message, withArgs)
                    ?? throw new InvalidOperationException("CanMessage.With() returned null");

                var messageId = (Guid)canMessageType.GetProperty("Id")!.GetValue(message)!;

                // Add to repository (writes to ICanMessageCache.CanMessages source-cache).
                msgRepoType.GetMethod("Add")!.Invoke(msgRepo, new[] { message });

                // Map direction string → CanMessageEcuLinkUsage enum value.
                object usageValue;
                try { usageValue = Enum.Parse(usageEnum, direction, ignoreCase: true); }
                catch (ArgumentException)
                {
                    throw new ArgumentException(
                        $"'direction' must be one of: SendCyclically, SendEventBased, " +
                        $"SendCyclicallyAndEventBased, SendMessageBuffer, Receive. Got '{direction}'.");
                }

                // Wrap as Nullable<CanMessageEcuLinkUsage> for the SetNewUsage signature.
                var nullableUsageType = typeof(Nullable<>).MakeGenericType(usageEnum);
                var nullableUsage = Activator.CreateInstance(nullableUsageType, usageValue);

                // SetNewUsage(messageId, ecuId, usage?) — creates the ECU link AND the
                // CSND/CRCV block via TRepository.Block, exactly like the GUI's Usage flip.
                var setNewUsage = linkSvcType.GetMethod("SetNewUsage")!;
                setNewUsage.Invoke(linkSvc, new object?[] { messageId, ecuId, nullableUsage });

                Program.WriteLog($"add_can_message: created '{name}' (id={messageId}) on bus {busOneIndexed} ({busId}) with usage={direction}");

                // Step 4: signals. Spec: "name:startbit:sizebits,name:startbit:sizebits,..."
                // Each becomes a CanSignal owned by the message. Default DataType = TUINT8
                // (matches v1's typical default — caller can override via data_type field
                // when we expose it; out of scope for this minimal step).
                var signalsResult = AddSignals(req, canContracts, businessIfaces, messageId);

                host.SaveProject();

                return RpcResponse.Ok(req.Id, new
                {
                    status = "ok",
                    message = $"Created CAN message '{name}' on bus {busOneIndexed} with usage {direction}",
                    name,
                    can_id = canId,
                    msg_guid = messageId.ToString(),
                    bus_id = busId.ToString(),
                    ecu_id = ecuId.ToString(),
                    direction,
                    signals = signalsResult,
                });
            }
            catch (Exception ex)
            {
                Program.WriteLog($"add_can_message failed: {ex}");
                return RpcResponse.Fail(req.Id, RpcErrorCodes.InternalError,
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        // v1 accepts can_id as int or hex string ("0x18FF..."). Support both.
        private static int ParseCanId(RpcRequest req)
        {
            var raw = req.GetStringParam("can_id");
            if (!string.IsNullOrEmpty(raw))
            {
                raw = raw.Trim();
                if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return Convert.ToInt32(raw.Substring(2), 16);
                if (int.TryParse(raw, out var asDec)) return asDec;
                return Convert.ToInt32(raw, 16);
            }
            return req.GetIntParam("can_id");
        }

        /// <summary>
        /// Parse the v1 signals string ("name:startbit:sizebits,...") and create a
        /// CanSignal per entry on the given message. Returns one summary dict per
        /// signal so the caller can echo them in the v1-shaped response.
        /// </summary>
        private static List<object> AddSignals(RpcRequest req, Assembly canContracts, Assembly businessIfaces, Guid messageId)
        {
            var spec = req.GetStringParam("signals", "").Trim();
            var result = new List<object>();
            if (string.IsNullOrEmpty(spec)) return result;

            var canSignalType = canContracts.GetType("Hydac.PDT.Can.Business.Contracts.Model.CanSignal", throwOnError: true)!;
            var sigRepoType = canContracts.GetType("Hydac.PDT.Can.Business.Contracts.Repository.ICanSignalRepository", throwOnError: true)!;
            var sigSvcType = canContracts.GetType("Hydac.PDT.Can.Business.Contracts.Signal.ISignalService", throwOnError: true)!;
            var dataTypeLoaderType = businessIfaces.GetType("Hydac.PDT.Business.Datapool.DataTransfer.IDataTypeLoader", throwOnError: true)!;

            var sigRepo = HostBootstrap.ResolveByReflection(sigRepoType)
                ?? throw new InvalidOperationException("ICanSignalRepository not registered");
            var sigSvc = HostBootstrap.ResolveByReflection(sigSvcType)
                ?? throw new InvalidOperationException("ISignalService not registered");
            var dtLoader = HostBootstrap.ResolveByReflection(dataTypeLoaderType)
                ?? throw new InvalidOperationException("IDataTypeLoader not registered");

            // Default datatype: TUINT8. For signals wider than 8 bits PDT auto-fits the
            // size internally; ISignalService.SetSizeBits below sets the actual size.
            var getByName = dataTypeLoaderType.GetMethod("GetByTypeName")!;
            var defaultDataType = getByName.Invoke(dtLoader, new object[] { "TUINT8" })
                ?? throw new InvalidOperationException("IDataTypeLoader.GetByTypeName('TUINT8') returned null");
            var dataTypeId = (Guid)defaultDataType.GetType().GetProperty("GUID")!.GetValue(defaultDataType)!;

            // CanSignal convenience ctor: (ownerId, startBit, name, dataTypeId, appDataTypeId).
            var simpleSigCtor = canSignalType.GetConstructor(new[] { typeof(Guid), typeof(int), typeof(string), typeof(Guid), typeof(Guid) })!;
            var addSig = sigRepoType.GetMethod("Add")!;
            var setSize = sigSvcType.GetMethod("SetSizeBits")!;

            foreach (var token in spec.Split(','))
            {
                var parts = token.Split(':');
                if (parts.Length != 3)
                    throw new ArgumentException($"signal entry must be 'name:startbit:sizebits' — got '{token}'");
                var sigName = parts[0].Trim();
                if (string.IsNullOrEmpty(sigName))
                    throw new ArgumentException($"signal name required in '{token}'");
                if (!int.TryParse(parts[1].Trim(), out var startBit))
                    throw new ArgumentException($"startBit must be int in '{token}'");
                if (!int.TryParse(parts[2].Trim(), out var sizeBits))
                    throw new ArgumentException($"sizeBits must be int in '{token}'");

                var signal = simpleSigCtor.Invoke(new object[] { messageId, startBit, sigName, dataTypeId, dataTypeId });
                addSig.Invoke(sigRepo, new[] { signal });

                if (sizeBits != 1)
                {
                    // SetSizeBits returns a new immutable CanSignal — repository.Update is
                    // called inside the service.
                    setSize.Invoke(sigSvc, new[] { signal, (object)sizeBits });
                }

                Program.WriteLog($"  signal: {sigName} startBit={startBit} sizeBits={sizeBits}");

                var sigId = (Guid)canSignalType.GetProperty("Id")!.GetValue(signal)!;
                result.Add(new
                {
                    name = sigName,
                    start_bit = startBit,
                    size_bits = sizeBits,
                    id = sigId.ToString(),
                });
            }

            return result;
        }
    }
}
