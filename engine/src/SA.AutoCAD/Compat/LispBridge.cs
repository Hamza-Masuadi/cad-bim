using System;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace SA.AutoCAD.Compat
{
    /// <summary>
    /// Evaluates an AutoLISP expression through acedEvaluateLisp, exported by accore.dll in every release this engine
    /// targets (x64 C++ mangled name below). It is not part of the documented managed API, which has no way to evaluate
    /// an arbitrary expression synchronously (Application.Invoke only calls functions already registered as external).
    /// A missing export surfaces as EntryPointNotFoundException / DllNotFoundException, which callers turn into
    /// unsupported_in_release instead of crashing.
    /// </summary>
    internal static class LispBridge
    {
        [DllImport("accore.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "?acedEvaluateLisp@@YAHPEB_WAEAPEAUresbuf@@@Z")]
        private static extern int acedEvaluateLisp(string expression, out IntPtr result);

        /// <summary>RTNORM from adscodes.h.</summary>
        private const int RtNorm = 5100;

        /// <summary>Returns the result buffer (caller disposes) or throws with the status code.</summary>
        internal static ResultBuffer? Evaluate(string expression)
        {
            int status = acedEvaluateLisp(expression, out IntPtr rb);
            if (status != RtNorm)
                throw new InvalidOperationException("acedEvaluateLisp returned status " + status + " (expected RTNORM " + RtNorm + ").");
            if (rb == IntPtr.Zero) return null;
            return (ResultBuffer)Autodesk.AutoCAD.Runtime.DisposableWrapper.Create(typeof(ResultBuffer), rb, true);
        }
    }
}
