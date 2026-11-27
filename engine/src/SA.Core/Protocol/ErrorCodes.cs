using System;
using System.Collections.Generic;

namespace SA.Core.Protocol
{
    /// <summary>The closed list of error codes a response may carry. Nothing else is valid on the wire.</summary>
    public static class ErrorCodes
    {
        /// <summary>The request is not valid JSON, misses fields, or names an unknown method.</summary>
        public const string BadRequest = "bad_request";
        /// <summary>The job does not match the op schemas (types, required fields, enums, refs).</summary>
        public const string SchemaInvalid = "schema_invalid";
        /// <summary>The job is well formed but does not fit the live document (missing layer, level, type...).</summary>
        public const string ValidationFailed = "validation_failed";
        /// <summary>The active document is not the one the job names.</summary>
        public const string WrongDocument = "wrong_document";
        /// <summary>The document's fingerprint differs from the job's.</summary>
        public const string DocumentChanged = "document_changed";
        /// <summary>A job with this id has already finished.</summary>
        public const string DuplicateJob = "duplicate_job";
        /// <summary>Another job is running.</summary>
        public const string Busy = "busy";
        /// <summary>A workshared element the job would change is owned by someone else.</summary>
        public const string NotOwned = "not_owned";
        public const string ReadOnlyDocument = "read_only_document";
        /// <summary>An op failed while executing; the job was rolled back.</summary>
        public const string OpFailed = "op_failed";
        /// <summary>The job was rolled back (readback mismatch, failed check or app error).</summary>
        public const string RolledBack = "rolled_back";
        public const string Timeout = "timeout";
        /// <summary>The op or feature does not exist in the running release.</summary>
        public const string UnsupportedInRelease = "unsupported_in_release";
        public const string Internal = "internal";

        public static readonly IReadOnlyList<string> All = new[]
        {
            BadRequest, SchemaInvalid, ValidationFailed, WrongDocument, DocumentChanged, DuplicateJob, Busy,
            NotOwned, ReadOnlyDocument, OpFailed, RolledBack, Timeout, UnsupportedInRelease, Internal,
        };

        public static bool IsKnown(string? code) => code != null && Array.IndexOf((string[])All, code) >= 0;
    }
}
