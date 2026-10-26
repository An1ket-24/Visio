using System.Text.Json;
using VisioGenerator;

string? input = null;
string? vsdxPath = null;

int checkIndex = Array.IndexOf(args, "--check");
if (checkIndex >= 0)
{
    if (checkIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("--check needs a file path: VisioGenerator --check out/file.vsdx");
        return 1;
    }

    string target = args[checkIndex + 1];
    var found = VsdxValidator.Validate(target);
    if (found.Count == 0)
    {
        Console.WriteLine($"OK: {Path.GetFullPath(target)} is a valid .vsdx package");
        return 0;
    }

    Console.Error.WriteLine($"INVALID: {Path.GetFullPath(target)} ({found.Count} issue(s))");
    foreach (var issue in found) Console.Error.WriteLine($"  - {issue}");
    return 2;
}

for (int i = 0; i < args.Length; i++)
{
    string a = args[i];

    if (a is "-h" or "--help")
    {
        Console.WriteLine("""
Usage: VisioGenerator [spec.json] [out.vsdx]

  spec.json   Riser spec (default: samples/chw-riser.json next to the exe)
  out.vsdx    Visio output (default: out/<spec name>.vsdx)

The generated .vsdx is validated (package structure, relationships,
XML well-formedness, shape integrity) before the command reports success.
""");
        return 0;
    }

    if (a.StartsWith("--"))
    {
        Console.Error.WriteLine($"Unknown option: {a}");
        return 1;
    }

    if (input is null) input = a;
    else if (vsdxPath is null) vsdxPath = a;
    else
    {
        Console.Error.WriteLine($"Unexpected argument: {a}");
        return 1;
    }
}

input ??= Path.Combine(AppContext.BaseDirectory, "samples", "chw-riser.json");

if (!File.Exists(input))
{
    Console.Error.WriteLine($"Spec not found: {input}");
    return 1;
}

var options = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
};

RiserSpec? spec;
try
{
    spec = JsonSerializer.Deserialize<RiserSpec>(await File.ReadAllTextAsync(input), options);
}
catch (JsonException ex)
{
    Console.Error.WriteLine($"Invalid spec: {ex.Message}");
    return 1;
}

if (spec is null)
{
    Console.Error.WriteLine("Spec is empty.");
    return 1;
}

Diagram diagram;
try
{
    diagram = LayoutBuilder.Build(spec);
}
catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
{
    Console.Error.WriteLine($"Invalid spec: {ex.Message}");
    return 1;
}

vsdxPath ??= Path.Combine("out", Path.GetFileNameWithoutExtension(input) + ".vsdx");

try
{
    VsdxWriter.Write(vsdxPath, diagram);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
{
    Console.Error.WriteLine($"Could not write {Path.GetFullPath(vsdxPath)}: {ex.Message}");

    if (ex is FileNotFoundException)
        Console.Error.WriteLine(
            "The 'templates' folder must sit next to the executable. " +
            "Rebuild with 'dotnet build' / 'dotnet publish' so it is copied to the output folder.");

    return 1;
}
Console.WriteLine($"Visio: {Path.GetFullPath(vsdxPath)}");
Console.WriteLine($"Page : {diagram.Width:0.##} x {diagram.Height:0.##} in, {diagram.Shapes.Count} shapes");

var issues = VsdxValidator.Validate(vsdxPath);
if (issues.Count > 0)
{
    Console.Error.WriteLine($"INVALID .vsdx ({issues.Count} issue(s)):");
    foreach (var issue in issues) Console.Error.WriteLine($"  - {issue}");
    return 2;
}

Console.WriteLine("Check: package valid (parts, relationships, XML, shapes)");
return 0;
