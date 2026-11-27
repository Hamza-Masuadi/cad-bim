using System;
using System.Text;

namespace SA.Cli
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            var app = new CliApp(Console.Out, Console.Error, new FileSessionStore(), new PipeConnector());
            int code = app.Run(args);
            Console.Out.Flush();
            // Exit explicitly: after a timeout the request thread may still be blocked on the pipe.
            Environment.Exit(code);
            return code;
        }
    }
}
