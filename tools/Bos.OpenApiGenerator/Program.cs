using System.Text;
using System.Text.Json;
using Bos.Application;
using Bos.Host;

if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("Usage: Bos.OpenApiGenerator OUTPUT_PATH");
    return 2;
}

try
{
    var output = Path.GetFullPath(args[0]);
    var document = ApiHost.OpenApi(MethodCatalogue.All);
    await File.WriteAllTextAsync(output, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
    Console.WriteLine(output);
    return 0;
}
catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("OpenAPI generation failed: " + error.Message);
    return 1;
}
