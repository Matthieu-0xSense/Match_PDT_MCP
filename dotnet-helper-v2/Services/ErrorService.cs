using System;
using System.Reflection;
using MatchPdt.Helper.Bootstrap;
using MatchPdt.Helper.Rpc;

namespace MatchPdt.Helper.Services
{
    /// <summary>
    /// Phase 2 target: replace v1 dotnet-helper's CustomErrorAdd (~600 lines of
    /// reflection-driven XML+BinaryFormatter mutation) with PDT's own services:
    ///
    ///   IErrorBuilder      (Hydac.PDT.Errors.Business.Contracts.AddOrRemove)
    ///       — ErrorBuilder.CreateError(Guid blockId, uint bit, IHymlError, IDetectionMethod)
    ///   IDetectionMethodLoader / IDetectionMethodTemplateLoader
    ///       — for finding existing templates and creating detection methods
    ///   IErrorBlockFactory / IErrorBlockBuilder
    ///       — for the "create new block" path (the ERR-block sibling-clone hack disappears)
    ///   IDetectionMethodFactory
    ///       — for the IDetectionMethod parameter to ErrorBuilder.CreateError
    ///
    /// Mapping the v1 JSON contract (template, dm_name, bit, spn, block_name, description,
    /// severity, fmi, fmi_extended, set/release_debounce_ms, set/release_threshold) onto
    /// these services is non-trivial: IHymlError comes from the knowledge base, and
    /// the project's existing TBlock structure has to be reconciled with what
    /// IErrorBlockFactory.Create produces. That mapping is the next commit.
    ///
    /// This file exists as the plumbing target for RpcLoop.Dispatch — it doesn't ship
    /// add_custom_error yet.
    /// </summary>
    internal static class ErrorService
    {
        public static RpcResponse AddCustomError(HostBootstrap host, RpcRequest req)
        {
            // TODO Phase 2: implement via IErrorBuilder + IErrorBlockFactory + IDetectionMethodFactory.
            // Until then, surface a clean MethodNotFound rather than throwing — the
            // Python server keeps using the v1 dotnet-helper for this verb.
            return RpcResponse.Fail(req.Id, RpcErrorCodes.MethodNotFound,
                "add_custom_error not yet implemented in v2; use v1 dotnet-helper");
        }
    }
}
