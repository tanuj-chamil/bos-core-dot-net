namespace Bos.Host;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] != "serve")
            return await OperatorCli.RunAsync(args);

        try
        {
            var options = HostOptions.ParseServe(args[1..]);
            await using var app = ApiHost.Build(options.DataDirectory, args[1..]);
            await app.RunAsync();
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Data.Common.DbException or Bos.Contracts.BosException or TypeInitializationException or DllNotFoundException or BadImageFormatException)
        {
            // Configuration/storage exceptions can contain paths or provider diagnostics.
            Console.Error.WriteLine("BOS could not start. Check the configuration, workspace ownership, and HTTPS certificate.");
            return 1;
        }
    }
}
