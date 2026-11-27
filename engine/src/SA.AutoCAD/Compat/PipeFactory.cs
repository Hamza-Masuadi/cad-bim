using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SA.AutoCAD.Compat
{
    /// <summary>
    /// Creates the engine's named pipe server instances with an explicit DACL: the current user gets full control,
    /// network logons are denied, nobody else is listed (so nobody else can connect).
    ///
    /// .NET Framework 4.8 takes the PipeSecurity in the NamedPipeServerStream constructor. On .NET 5+ that constructor
    /// overload does not exist; the documented replacement is NamedPipeServerStreamAcl.Create (System.IO.Pipes.AccessControl).
    /// PipeOptions.CurrentUserOnly is deliberately not combined with the explicit DACL: the explicit DACL is the one
    /// mechanism that exists in all four builds, so the security behaviour is the same in every release.
    /// </summary>
    internal static class PipeFactory
    {
        internal static PipeSecurity CreateSecurity()
        {
            var security = new PipeSecurity();
            using (var me = WindowsIdentity.GetCurrent())
            {
                var user = me.User ?? throw new System.InvalidOperationException("The current Windows identity has no user SID.");
                security.SetOwner(user);
                security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            }
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            // No inherited entries: only the rules above apply.
            security.SetAccessRuleProtection(true, false);
            return security;
        }

        /// <param name="first">True for the first instance. On .NET 8/10 PipeOptions.FirstPipeInstance makes creation fail
        /// if another process already owns a pipe with this name (name squatting). .NET Framework rejects that option, so
        /// for every release the first instance is also checked after creation: its owner must be the current user,
        /// otherwise we joined someone else's pipe and its security is theirs.</param>
        internal static NamedPipeServerStream Create(string name, int maxInstances, bool first)
        {
            var options = PipeOptions.Asynchronous | PipeOptions.WriteThrough;
            var security = CreateSecurity();
#if Y2025_OR_GREATER
            if (first) options |= PipeOptions.FirstPipeInstance;
            var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte,
                options, 64 * 1024, 64 * 1024, security);
#else
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte,
                options, 64 * 1024, 64 * 1024, security);
#endif
            if (first)
            {
                try
                {
                    VerifyOwner(pipe);
                }
                catch
                {
                    pipe.Dispose();
                    throw;
                }
            }
            return pipe;
        }

        private static void VerifyOwner(NamedPipeServerStream pipe)
        {
#if Y2025_OR_GREATER
            var acl = PipesAclExtensions.GetAccessControl(pipe);
#else
            var acl = pipe.GetAccessControl();
#endif
            var owner = acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            using (var me = WindowsIdentity.GetCurrent())
            {
                if (owner is null || me.User is null || !owner.Equals(me.User))
                    throw new System.UnauthorizedAccessException("The pipe exists already and is owned by " + (owner?.Value ?? "nobody") +
                        "; refusing to serve on a pipe this user does not own.");
            }
        }
    }
}
