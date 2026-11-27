using System;
using SA.Core.Json;

namespace SA.Core.Protocol
{
    /// <summary>Thrown when a request or response does not follow the protocol. Maps to <see cref="ErrorCodes.BadRequest"/>.</summary>
    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message) { }
        public ProtocolException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary><c>{ "v": 1, "id": "...", "method": "run", "params": { } }</c></summary>
    public sealed class Request
    {
        public Request(string id, string method, JsonObject? parameters = null)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("Request id is required.", nameof(id));
            if (string.IsNullOrEmpty(method)) throw new ArgumentException("Method is required.", nameof(method));
            Id = id;
            Method = method;
            Params = parameters ?? new JsonObject();
        }

        public int V => ProtocolInfo.Version;
        public string Id { get; }
        public string Method { get; }
        public JsonObject Params { get; }

        public static Request Create(string method, JsonObject? parameters = null) =>
            new Request(Guid.NewGuid().ToString("D"), method, parameters);

        public JsonObject ToJson() => new JsonObject()
            .Add("v", ProtocolInfo.Version)
            .Add("id", Id)
            .Add("method", Method)
            .Add("params", Params);

        /// <summary>Parses and checks a request. Unknown methods are accepted here; the engine answers them with bad_request.</summary>
        public static Request Parse(JsonValue json)
        {
            try
            {
                var c = new JsonCursor(json);
                c.RejectUnknownKeys("v", "id", "method", "params");
                int v = c.GetInt32("v");
                if (v != ProtocolInfo.Version) throw new ProtocolException("Unsupported protocol version " + v + "; this build speaks version " + ProtocolInfo.Version + ".");
                string id = c.GetNonEmptyString("id");
                string method = c.GetNonEmptyString("method");
                var p = c.Optional("params");
                return new Request(id, method, p?.AsObject() ?? new JsonObject());
            }
            catch (JsonAccessException e)
            {
                throw new ProtocolException("Malformed request: " + e.Message, e);
            }
        }

        public static Request Parse(byte[] utf8)
        {
            JsonValue json;
            try { json = JsonValue.Parse(utf8); }
            catch (JsonParseException e) { throw new ProtocolException(e.Message, e); }
            return Parse(json);
        }
    }

    public sealed class ResponseError
    {
        public ResponseError(string code, string message, JsonObject? details = null)
        {
            if (!ErrorCodes.IsKnown(code)) throw new ArgumentException("Error code '" + code + "' is not in the closed list.", nameof(code));
            Code = code;
            Message = message ?? "";
            Details = details;
        }

        public string Code { get; }
        public string Message { get; }
        public JsonObject? Details { get; }

        public JsonObject ToJson()
        {
            var o = new JsonObject().Add("code", Code).Add("message", Message);
            if (Details != null) o.Add("details", Details);
            return o;
        }
    }

    /// <summary>
    /// <c>{ "v": 1, "id": "...", "ok": true, "result": { } }</c> or
    /// <c>{ "v": 1, "id": "...", "ok": false, "error": { "code": "...", "message": "...", "details": { } } }</c>
    /// </summary>
    public sealed class Response
    {
        private Response(string id, bool ok, JsonValue? result, ResponseError? error)
        {
            Id = id;
            Ok = ok;
            Result = result;
            Error = error;
        }

        public int V => ProtocolInfo.Version;
        /// <summary>The request id, or "" when the request could not be read far enough to know it.</summary>
        public string Id { get; }
        public bool Ok { get; }
        public JsonValue? Result { get; }
        public ResponseError? Error { get; }

        public static Response Success(string id, JsonValue? result) => new Response(id ?? "", true, result ?? new JsonObject(), null);

        public static Response Failure(string id, string code, string message, JsonObject? details = null) =>
            new Response(id ?? "", false, null, new ResponseError(code, message, details));

        public JsonObject ToJson()
        {
            var o = new JsonObject().Add("v", ProtocolInfo.Version).Add("id", Id).Add("ok", Ok);
            if (Ok) o.Add("result", Result ?? new JsonObject());
            else o.Add("error", Error!.ToJson());
            return o;
        }

        public static Response Parse(JsonValue json)
        {
            try
            {
                var c = new JsonCursor(json);
                c.RejectUnknownKeys("v", "id", "ok", "result", "error");
                int v = c.GetInt32("v");
                if (v != ProtocolInfo.Version) throw new ProtocolException("Unsupported protocol version " + v + ".");
                string id = c.GetString("id");
                bool ok = c.Required("ok").AsBool();
                if (ok)
                {
                    if (c.Has("error")) throw new ProtocolException("A successful response must not carry 'error'.");
                    return Success(id, c.Optional("result")?.Value ?? new JsonObject());
                }
                if (c.Has("result")) throw new ProtocolException("A failed response must not carry 'result'.");
                var e = c.Required("error");
                string code = e.GetString("code");
                if (!ErrorCodes.IsKnown(code)) throw new ProtocolException("Unknown error code '" + code + "'.");
                return Failure(id, code, e.GetString("message", ""), e.Optional("details")?.AsObject());
            }
            catch (JsonAccessException e)
            {
                throw new ProtocolException("Malformed response: " + e.Message, e);
            }
        }

        public static Response Parse(byte[] utf8)
        {
            JsonValue json;
            try { json = JsonValue.Parse(utf8); }
            catch (JsonParseException e) { throw new ProtocolException(e.Message, e); }
            return Parse(json);
        }
    }
}
