using System;
using SA.Core.Json;
using SA.Core.Protocol;

namespace SA.AutoCAD.Host
{
    /// <summary>A refusal or failure that maps to one wire error code from the closed list in <see cref="ErrorCodes"/>.</summary>
    internal sealed class EngineException : Exception
    {
        internal EngineException(string code, string message, JsonObject? details = null, Exception? inner = null)
            : base(message, inner)
        {
            if (!ErrorCodes.IsKnown(code)) throw new ArgumentException("Unknown error code '" + code + "'.", nameof(code));
            Code = code;
            Details = details;
        }

        internal string Code { get; }
        internal JsonObject? Details { get; }
    }
}
