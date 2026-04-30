using KubernetesClient.StrategicPatch.SchemaTool;

if (args.Length < 2)
{
    Console.Error.WriteLine(
        "Usage: KubernetesClient.StrategicPatch.SchemaTool <output-schemas.json> <openapi-v3-input>...");
    return 64; // EX_USAGE
}

try
{
    var output = args[0];
    var inputs = args.Skip(1).ToArray();
    var count = SchemaToolRunner.Run(output, inputs);
    Console.WriteLine(
        $"SchemaTool: wrote {count} GVKs → {output} (from {inputs.Length} OpenAPI input(s))");
    return 0;
}
catch (FileNotFoundException ex)
{
    Console.Error.WriteLine($"::error::{ex.Message}");
    return 66; // EX_NOINPUT
}
catch (Exception ex)
{
    Console.Error.WriteLine($"::error::SchemaTool failed: {ex.Message}");
    return 70; // EX_SOFTWARE
}
