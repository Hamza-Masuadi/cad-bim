using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SA.Revit.Compat
{
    /// <summary>
    /// Creates the engine's pipe instances, restricted to the current user.
    ///
    /// .NET Framework 4.8 (Revit 2024) takes a <see cref="PipeSecurity"/> in a NamedPipeServerStream constructor.
    /// .NET 5 and later dropped that constructor; the same thing is done with NamedPipeServerStreamAcl.Create
    /// (System.IO.Pipes.AccessControl). The ACL is identical in both: the current user's SID gets full control,
    /// the NETWORK SID is denied, and inherited entries are removed, so neither other local accounts nor remote
    /// clients can open the pipe. PipeOptions.CurrentUserOnly is deliberately not used: it does not exist on 4.8 and
    /// it also enforces a matching elevation level, which would make 2024 and 2025+ behave differently.
    /// </summary>
    internal static class PipeFactory
    {
        public const int BufferSize = 64 * 1024;

        public static PipeSecurity CurrentUserOnly()
        {
            var security = new PipeSecurity();
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var user = identity.User ?? throw new System.InvalidOperationException("The current Windows identity has no user SID.");
                security.SetOwner(user);
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                    PipeAccessRights.FullControl, AccessControlType.Deny));
            }
            return security;
        }

        /// <param name="firstInstance">
        /// True for the very first instance: creation then fails if any process already owns a pipe with this name,
        /// which stops another program squatting on the name before the engine starts.
        /// </param>
        public static NamedPipeServerStream Create(string pipeName, int maxInstances, bool firstInstance)
        {
            var options = PipeOptions.Asynchronous | PipeOptions.WriteThrough;
            var security = CurrentUserOnly();
#if Y2025_OR_GREATER
            if (firstInstance) options |= PipeOptions.FirstPipeInstance;
            return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte,
                options, BufferSize, BufferSize, security);
#else
            // .NET Framework has no PipeOptions.FirstPipeInstance (and rejects unknown option bits), so ask Windows
            // whether any instance of the name exists before creating ours.
            if (firstInstance && PipeExists(pipeName))
                throw new System.IO.IOException("A pipe named '" + pipeName + "' already exists; another process owns the name.");
            return new NamedPipeServerStream(pipeName, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte,
                options, BufferSize, BufferSize, security);
#endif
        }

#if !Y2025_OR_GREATER
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool WaitNamedPipe(string name, uint timeoutMs);

        private const int ErrorFileNotFound = 2;

        /// <summary>WaitNamedPipe fails with ERROR_FILE_NOT_FOUND only when no instance of the name exists.</summary>
        private static bool PipeExists(string pipeName)
        {
            if (WaitNamedPipe(@"\\.\pipe\" + pipeName, 1)) return true;
            return System.Runtime.InteropServices.Marshal.GetLastWin32Error() != ErrorFileNotFound;
        }
#endif

        /// <summary>The runtime the engine was built for, for ping and logs.</summary>
        public static string TargetRuntime =>
#if Y2027
            "net10.0-windows";
#elif Y2025_OR_GREATER
            "net8.0-windows";
#else
            "net48";
#endif
    }
}
