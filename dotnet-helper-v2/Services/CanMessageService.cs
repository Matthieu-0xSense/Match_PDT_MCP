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
            // TODO Phase 3 implementation.
            // Wire the JSON contract → service calls:
            //   1. Resolve ICanMessageRepository, ICanMessageEcuLinkRepository,
            //      ICanMessageEcuLinkService, ICanSignalRepository (or equivalent).
            //   2. Find bus by 1-indexed input.bus → busId, find associated send/recv
            //      buffer blocks and CAN block (mirror _get_bus_info in server.py).
            //   3. Construct CanMessage with the 16-arg ctor, validate uniqueness.
            //   4. ICanMessageRepository.Add(canMessage).
            //   5. Construct CanMessageEcuLink with messageId, ecuId, Usage.None,
            //      buffer/canBlock GUIDs.
            //   6. ICanMessageEcuLinkRepository.Add(ecuLink).
            //   7. For each signal definition, construct CanSignal via
            //      ICanSignalRepository.Add (or via ITRepository.NewCanSignal if it exists).
            //   8. ICanMessageEcuLinkService.SetNewUsage(messageId, ecuLinkId, mapped Usage)
            //      to trigger CSND/CRCV block creation — this is the killer feature
            //      that kills v1's "set Usage in PDT manually" caveat.
            //   9. host.SaveProject() — extend HostBootstrap.WriteErrorsDat-style
            //      pattern with WriteCanMessagesDat covering the CAN-side persistence types.
            //  10. Return v1-shaped result: { status, message, name, can_id, msg_guid, signals }.
            return RpcResponse.Fail(req.Id, RpcErrorCodes.MethodNotFound,
                "add_can_message v2 not yet implemented; server.py keeps using v1");
        }
    }
}
