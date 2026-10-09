using System.Runtime.Loader;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Homestead.Features.Themes;

// Templates are trusted server code. Compilation validates syntax; it is not a sandbox.
public sealed class RazorThemeCompiler : IDisposable
{
    public static readonly string[] TemplateNames = ["Main", "Header", "Content", "Footer", "Post", "Note", "Article", "Review", "Guide", "Gallery", "Checkin"];
    private readonly object gate = new();
    private string? cachedId;
    private CompiledTheme? cached;
    private static readonly Lazy<MetadataReference[]> References = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(ThemeComponent).Assembly.Location).Distinct(StringComparer.Ordinal)
            .Select(file => MetadataReference.CreateFromFile(file)).ToArray());

    public void Validate(ThemePackage theme)
    {
        if (theme.Variant.Templates is not { Count: > 0 }) return;
        using var compiled = Compile(theme);
    }

    public Type? Resolve(ThemePackage theme, string name)
    {
        if (theme.Variant.Templates is not { Count: > 0 }) return null;
        lock (gate)
        {
            if (cachedId != theme.Id)
            {
                var next = Compile(theme);
                cached?.Dispose();
                cached = next;
                cachedId = theme.Id;
            }
            return cached!.Types.GetValueOrDefault(name);
        }
    }

    private static CompiledTheme Compile(ThemePackage theme)
    {
        var engine = RazorProjectEngine.Create(RazorConfiguration.Default, RazorProjectFileSystem.Create("/"),
            builder => builder.SetRootNamespace("HomesteadTheme"));
        var imports = RazorSourceDocument.Create("""
            @using System
            @using System.Linq
            @using Microsoft.AspNetCore.Components
            @using Homestead.Features.Themes
            @using Homestead.Data.Entities
            @inherits ThemeComponent
            """, "/_Imports.razor");
        var trees = new List<SyntaxTree>();
        foreach (var (name, file) in theme.Variant.Templates!)
        {
            // Logical names provide deterministic classes independently of the package directory.
            var source = RazorSourceDocument.Create(Encoding.UTF8.GetString(theme.Files[file]),
                new RazorSourceDocumentProperties("/" + name + ".razor", name + ".razor"));
            var document = engine.Process(source, FileKinds.Component, [imports], []);
            var generated = document.GetCSharpDocument();
            var errors = generated.Diagnostics.Where(x => x.Severity == RazorDiagnosticSeverity.Error).ToArray();
            if (errors.Length > 0) throw new InvalidDataException($"Razor template {file}: {string.Join("\n", errors.Take(20).Select(x => x.ToString()))}");
            if (generated.Diagnostics.Any(x => x.Id == "RZ10012"))
                throw new InvalidDataException($"Razor template {file}: use HTML elements and Model's rendered regions; child component tags are not supported.");
            trees.Add(CSharpSyntaxTree.ParseText(generated.GeneratedCode, path: file));
        }
        var compilation = CSharpCompilation.Create("HomesteadTheme_" + Guid.NewGuid().ToString("N"), trees, References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        if (!result.Success)
            throw new InvalidDataException("Razor theme compilation failed:\n" + string.Join("\n", result.Diagnostics
                .Where(x => x.Severity == DiagnosticSeverity.Error).Take(20).Select(x => x.ToString())));
        output.Position = 0;
        var context = new AssemblyLoadContext(compilation.AssemblyName, isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(output);
            var types = theme.Variant.Templates.Keys.ToDictionary(name => name,
                name => assembly.GetTypes().SingleOrDefault(type => type.Name == name)
                    ?? throw new InvalidDataException($"Razor template {name} did not produce its component class."));
            if (types.Values.Any(type => !typeof(ThemeComponent).IsAssignableFrom(type)))
                throw new InvalidDataException("Razor templates must inherit ThemeComponent.");
            return new(context, types);
        }
        catch { context.Unload(); throw; }
    }

    public void Dispose()
    {
        lock (gate) { cached?.Dispose(); cached = null; cachedId = null; }
    }

    private sealed record CompiledTheme(AssemblyLoadContext Context, Dictionary<string, Type> Types) : IDisposable
    {
        public void Dispose() => Context.Unload();
    }
}

public abstract class ThemeComponent : ComponentBase
{
    [Parameter, EditorRequired] public ThemePageModel Model { get; set; } = null!;
}
