using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Spec for the elevated DLL search bug.
//
// The shipped app is one self-contained executable (scripts/release.sh sets PublishSingleFile).
// Include admin windows relaunches that same executable with the runas verb
// (src/Expanse/Elevation.cs StartElevated, src/Expanse/Program.cs). The unzip folder is in the
// user profile (README install section), so a program running as that user can write beside the exe.
//
// On .NET 10 (dotnet/runtime tag v10.0.15, the runtime this SDK ships) a
// P/Invoke with no DefaultDllImportSearchPaths probes the assembly directory before the OS search.
// For a single-file bundle that directory is the directory of the executable
// (peimage.inl GetPathToLoad, bundle.cpp Bundle::Path). The probe calls LoadLibraryExW on that
// full path (nativelibrary.cpp LoadFromPInvokeAssemblyDirectory, util.cpp CLRLoadLibraryEx,
// winwrap.h WszLoadLibrary, longfilepathwrappers.cpp LoadLibraryExWrapper). LoadLibraryExW loads
// the module at a fully qualified path. These tests plant libraries in that directory and require
// the elevated startup path not to choose them.
//
// They fail while that choice is the application directory. They pass when each load is restricted
// to System32 (or the call that triggered it is gone).

const uint LoadWithAlteredSearchPath = 0x8;
const uint LoadLibrarySearchDllLoadDir = 0x100;
const uint LoadLibrarySearchApplicationDir = 0x200;
const uint LoadLibrarySearchUserDirs = 0x400;
const uint LoadLibrarySearchSystem32 = 0x800;
const uint LoadLibrarySearchDefaultDirs = 0x1000;
const uint AssemblyDirectoryBit = 0x2;
const uint LoadLibrarySearchFlagMask = LoadLibrarySearchDllLoadDir | LoadLibrarySearchApplicationDir
    | LoadLibrarySearchUserDirs | LoadLibrarySearchSystem32 | LoadLibrarySearchDefaultDirs;

// Names Windows has published on the KnownDLLs list (registry) or the \KnownDlls section
// (a listed DLL plus its imports) from Windows 8 through Windows 10 1803. A fully qualified
// LoadLibraryEx is documented to load that path; these names are still not asserted, because
// that list may short-circuit them and this environment cannot boot Windows 11 to check.
var knownDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "advapi32.dll", "clbcatq.dll", "combase.dll", "comctl32.dll", "comdlg32.dll", "coml2.dll",
    "crypt32.dll", "difxapi.dll", "fltlib.dll", "gdi32.dll", "gdi32full.dll", "gdiplus.dll",
    "imagehlp.dll", "imm32.dll", "kernel32.dll", "kernelbase.dll", "msasn1.dll", "msctf.dll",
    "msvcp_win.dll", "msvcrt.dll", "normaliz.dll", "nsi.dll", "ntdll.dll", "ole32.dll",
    "oleaut32.dll", "powrprof.dll", "profapi.dll", "psapi.dll", "rpcrt4.dll", "sechost.dll",
    "setupapi.dll", "shcore.dll", "shell32.dll", "shlwapi.dll", "ucrtbase.dll", "user32.dll",
    "win32u.dll", "windows.storage.dll", "wintrust.dll", "wldap32.dll", "ws2_32.dll",
};

string root = FindRepo();
string installDir = Path.Combine(Path.GetTempPath(), "expanse-dll-search-" + Guid.NewGuid().ToString("n"));
Directory.CreateDirectory(installDir);
var failures = new List<string>();
try
{
    bool singleFile = File.ReadAllText(Path.Combine(root, "scripts", "release.sh"))
        .Contains("PublishSingleFile=true", StringComparison.Ordinal);
    if (!singleFile)
        failures.Add("release.sh no longer publishes a single file; the bundle-directory model needs revisiting");

    var trees = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
        .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}DllSearch.Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        .Select(p => CSharpSyntaxTree.ParseText(File.ReadAllText(p), path: p))
        .ToList();

    var productImports = ProductImports(trees, root);
    var vorticeDll = VorticeDirect2D1Dll(root);
    var vorticeImports = MetadataImports(vorticeDll, "Vortice.Direct2D1");
    bool relaunchSameExe = RelaunchesThisExecutable(trees);
    var resolvers = ResolversBeforeCanvas(trees);
    var asserted = StartupImports(trees, productImports, vorticeImports, singleFile);

    Console.WriteLine($"install directory (user-writable): {installDir}");
    Console.WriteLine($"single-file publish: {singleFile}; relaunch uses this executable: {relaunchSameExe}");
    Console.WriteLine($"resolvers registered before the canvas: {string.Join(", ", resolvers.Select(r => r.TypeName))}");

    foreach (var import in asserted)
    {
        if (knownDlls.Contains(import.Library))
        {
            Console.WriteLine($"SKIP {import.Library} from {import.Owner} ({import.Why}); on the published KnownDLL list, not asserted");
            continue;
        }
        string planted = Path.Combine(installDir, import.Library);
        File.WriteAllBytes(planted, "MZ planted library"u8.ToArray());
        var decision = Resolve(import, installDir, singleFile, resolvers);
        bool safe = decision.ChosenPath == null
            || !decision.ChosenPath.StartsWith(installDir, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"{(safe ? "PASS" : "FAIL")} {import.Why}: {import.Owner} {import.Library} -> {decision.Describe()}");
        if (!safe)
            failures.Add($"{import.Library} ({import.Why}) loads {decision.ChosenPath} ({decision.How})");
    }
}
finally
{
    try { Directory.Delete(installDir, recursive: true); }
    catch (IOException) { }
}

if (failures.Count == 0)
{
    Console.WriteLine("PASS elevated startup does not load a library planted beside the executable");
    return 0;
}
Console.WriteLine($"{failures.Count} failure(s):");
foreach (var failure in failures)
    Console.WriteLine("  " + failure);
return 1;

static string FindRepo()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "src", "Expanse", "Elevation.cs")))
            return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("repository root not found from " + AppContext.BaseDirectory);
}

static string VorticeDirect2D1Dll(string root)
{
    string version = "3.8.3";
    foreach (var line in File.ReadLines(Path.Combine(root, "src", "Expanse", "Expanse.csproj")))
    {
        const string mark = "Include=\"Vortice.Direct2D1\" Version=\"";
        int at = line.IndexOf(mark, StringComparison.Ordinal);
        if (at < 0)
            continue;
        int start = at + mark.Length;
        int end = line.IndexOf('"', start);
        version = line[start..end];
        break;
    }
    var roots = new List<string>();
    if (Environment.GetEnvironmentVariable("NUGET_PACKAGES") is string env && env.Length > 0)
        roots.Add(env);
    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    roots.Add(Path.Combine(home, ".nuget", "packages"));
    roots.Add(Path.Combine(home, ".dotnet", ".nuget", "packages"));
    foreach (var packages in roots)
    {
        string dll = Path.Combine(packages, "vortice.direct2d1", version, "lib", "net10.0", "Vortice.Direct2D1.dll");
        if (File.Exists(dll))
            return dll;
    }
    throw new FileNotFoundException(
        "Vortice.Direct2D1 " + version + " was not restored. Run dotnet restore on src/DllSearch.Tests.",
        Path.Combine(roots[0], "vortice.direct2d1", version));
}

// --- product and package imports ------------------------------------------------

static List<Import> ProductImports(List<SyntaxTree> trees, string repo)
{
    var imports = new List<Import>();
    uint? assemblyFlags = null;
    bool assemblySpecified = false;
    string nativeRoot = Path.GetFullPath(Path.Combine(repo, "src", "Native")) + Path.DirectorySeparatorChar;
    string expanseRoot = Path.GetFullPath(Path.Combine(repo, "src", "Expanse")) + Path.DirectorySeparatorChar;
    foreach (var tree in trees)
    {
        string path = Path.GetFullPath(tree.FilePath);
        if (!path.StartsWith(expanseRoot, StringComparison.Ordinal) && !path.StartsWith(nativeRoot, StringComparison.Ordinal))
            continue;
        var root = tree.GetRoot();
        foreach (var attr in root.DescendantNodes().OfType<AttributeListSyntax>().Where(a => a.Target?.Identifier.IsKind(SyntaxKind.AssemblyKeyword) == true)
                     .SelectMany(a => a.Attributes))
        {
            if (attr.Name.ToString().Contains("DefaultDllImportSearchPaths", StringComparison.Ordinal)
                && TryParseSearchFlags(attr, out uint flags))
            {
                assemblySpecified = true;
                assemblyFlags = flags;
            }
        }
    }
    foreach (var tree in trees)
    {
        string path = Path.GetFullPath(tree.FilePath);
        if (!path.StartsWith(expanseRoot, StringComparison.Ordinal) && !path.StartsWith(nativeRoot, StringComparison.Ordinal))
            continue;
        foreach (var method in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            AttributeSyntax? dllImport = null;
            uint? methodFlags = assemblyFlags;
            bool specified = assemblySpecified;
            foreach (var attr in method.AttributeLists.SelectMany(l => l.Attributes))
            {
                string name = attr.Name.ToString();
                if (name is "DllImport" or "LibraryImport" || name.EndsWith(".DllImport", StringComparison.Ordinal) || name.EndsWith(".LibraryImport", StringComparison.Ordinal))
                    dllImport = attr;
                else if (name.Contains("DefaultDllImportSearchPaths", StringComparison.Ordinal) && TryParseSearchFlags(attr, out uint flags))
                {
                    specified = true;
                    methodFlags = flags;
                }
            }
            if (dllImport?.ArgumentList?.Arguments.Count is not > 0)
                continue;
            string? library = ResolveLibrary(dllImport.ArgumentList.Arguments[0].Expression, method);
            if (library == null)
                continue;
            imports.Add(new Import(NormalizeDll(library), "Expanse", true, methodFlags, specified, method.Identifier.Text, method.Identifier.Text));
        }
    }
    return imports;
}

static string? ResolveLibrary(ExpressionSyntax expression, MethodDeclarationSyntax method)
{
    if (expression is LiteralExpressionSyntax literal && literal.Token.ValueText is string text)
        return text;
    if (expression is IdentifierNameSyntax id)
    {
        var type = method.Parent as TypeDeclarationSyntax;
        var field = type?.Members.OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(f => f.Modifiers.Any(SyntaxKind.ConstKeyword)
                && f.Declaration.Variables.Any(v => v.Identifier.Text == id.Identifier.Text));
        if (field?.Declaration.Variables.First(v => v.Identifier.Text == id.Identifier.Text).Initializer?.Value
            is LiteralExpressionSyntax init && init.Token.ValueText is string value)
            return value;
    }
    return null;
}

static bool TryParseSearchFlags(AttributeSyntax attr, out uint flags)
{
    flags = 0;
    if (attr.ArgumentList == null)
        return false;
    foreach (var arg in attr.ArgumentList.Arguments)
        flags |= ParseFlagExpr(arg.Expression);
    return true;
}

static uint ParseFlagExpr(ExpressionSyntax expression) => expression switch
{
    BinaryExpressionSyntax { RawKind: (int)SyntaxKind.BitwiseOrExpression } orExpr =>
        ParseFlagExpr(orExpr.Left) | ParseFlagExpr(orExpr.Right),
    MemberAccessExpressionSyntax member => FlagName(member.Name.Identifier.Text),
    IdentifierNameSyntax id => FlagName(id.Identifier.Text),
    LiteralExpressionSyntax literal when literal.Token.Value is int n => (uint)n,
    _ => 0,
};

static uint FlagName(string name) => name switch
{
    "AssemblyDirectory" => AssemblyDirectoryBit,
    "UseDllDirectoryForDependencies" => LoadLibrarySearchDllLoadDir,
    "ApplicationDirectory" => LoadLibrarySearchApplicationDir,
    "UserDirectories" => LoadLibrarySearchUserDirs,
    "System32" => LoadLibrarySearchSystem32,
    "SafeDirectories" => LoadLibrarySearchDefaultDirs,
    "LegacyBehavior" => 0,
    _ => 0,
};

static string NormalizeDll(string library)
{
    string name = library.Replace('/', '\\');
    int slash = name.LastIndexOf('\\');
    if (slash >= 0)
        name = name[(slash + 1)..];
    if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !name.Contains('.', StringComparison.Ordinal))
        name += ".dll";
    return name;
}

static List<Import> MetadataImports(string path, string owner)
{
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    var reader = pe.GetMetadataReader();
    var imports = new List<Import>();
    foreach (var handle in reader.MethodDefinitions)
    {
        var method = reader.GetMethodDefinition(handle);
        string? library = null;
        if ((method.Attributes & System.Reflection.MethodAttributes.PinvokeImpl) != 0)
        {
            var map = method.GetImport();
            library = reader.GetString(reader.GetModuleReference(map.Module).Name);
        }
        uint? flags = null;
        bool specified = false;
        foreach (var attrHandle in method.GetCustomAttributes())
        {
            var attr = reader.GetCustomAttribute(attrHandle);
            string typeName = AttributeTypeName(reader, attr);
            if (library == null && typeName is "DllImportAttribute" or "LibraryImportAttribute")
                library = FirstStringArg(reader, attr);
            else if (typeName is "DefaultDllImportSearchPathsAttribute")
            {
                specified = true;
                flags = FirstEnumArg(reader, attr);
            }
        }
        if (library == null)
            continue;
        string methodName = reader.GetString(method.Name);
        imports.Add(new Import(NormalizeDll(library), owner, true, flags, specified, methodName, methodName));
    }
    return imports;
}

static string AttributeTypeName(MetadataReader reader, CustomAttribute attr)
{
    EntityHandle ctor = attr.Constructor;
    if (ctor.Kind == HandleKind.MemberReference)
    {
        var member = reader.GetMemberReference((MemberReferenceHandle)ctor);
        if (member.Parent.Kind == HandleKind.TypeReference)
            return reader.GetString(reader.GetTypeReference((TypeReferenceHandle)member.Parent).Name);
    }
    return "";
}

static string? FirstStringArg(MetadataReader reader, CustomAttribute attr)
{
    var readerBlob = reader.GetBlobReader(attr.Value);
    if (readerBlob.ReadUInt16() != 1)
        return null;
    return readerBlob.ReadSerializedString();
}

static uint FirstEnumArg(MetadataReader reader, CustomAttribute attr)
{
    var readerBlob = reader.GetBlobReader(attr.Value);
    if (readerBlob.ReadUInt16() != 1 || readerBlob.RemainingBytes < 4)
        return 0;
    return readerBlob.ReadUInt32();
}

// --- which loads the elevated process actually reaches --------------------------------

static List<Import> StartupImports(List<SyntaxTree> trees, List<Import> product, List<Import> vortice, bool singleFile)
{
    string text = string.Join("\n", trees.Select(t => t.ToString()));
    var list = new List<Import>();
    void AddProduct(string library, string why)
    {
        foreach (var import in product.Where(i => i.Library.Equals(library, StringComparison.OrdinalIgnoreCase)))
            list.Add(import with { Why = why + " (" + import.Method + ")", InBundle = true });
    }
    if (text.Contains("D3D11CreateDevice", StringComparison.Ordinal))
        AddProduct("d3d11.dll", "canvas construction calls D3D11CreateDevice");
    if (text.Contains("DCompositionCreateDevice3", StringComparison.Ordinal))
        AddProduct("dcomp.dll", "canvas construction calls DCompositionCreateDevice3");
    if (text.Contains("FrameBounds", StringComparison.Ordinal) || text.Contains("DwmpCreateSharedThumbnailVisual", StringComparison.Ordinal))
        AddProduct("dwmapi.dll", "the elevated process calls dwmapi");
    if (text.Contains("D2D1CreateFactory", StringComparison.Ordinal))
    {
        var factory = vortice.Where(i => i.Library.Equals("d2d1.dll", StringComparison.OrdinalIgnoreCase)
            && i.Method.StartsWith("D2D1CreateFactory", StringComparison.Ordinal)).ToList();
        if (factory.Count == 0)
            throw new InvalidOperationException("Vortice.Direct2D1 3.8.3 has no D2D1CreateFactory P/Invoke");
        foreach (var import in factory)
            list.Add(import with { Why = "canvas construction calls D2D1.D2D1CreateFactory", InBundle = singleFile });
    }
    if (text.Contains("DWriteCreateFactory", StringComparison.Ordinal))
    {
        var factory = vortice.Where(i => i.Library.Equals("dwrite.dll", StringComparison.OrdinalIgnoreCase)
            && i.Method.StartsWith("DWriteCreateFactory", StringComparison.Ordinal)).ToList();
        if (factory.Count == 0)
            throw new InvalidOperationException("Vortice.Direct2D1 3.8.3 has no DWriteCreateFactory P/Invoke");
        foreach (var import in factory)
            list.Add(import with { Why = "canvas construction calls DWrite.DWriteCreateFactory", InBundle = singleFile });
    }
    if (text.Contains("FileVersionInfo.GetVersionInfo", StringComparison.Ordinal))
    {
        // System.Diagnostics.FileVersionInfo on .NET 10 Windows:
        // FileVersionInfo.Windows.cs calls Interop.Version.GetFileVersionInfoSizeEx.
        // Interop.GetFileVersionInfoSizeEx.cs is [LibraryImport(Libraries.Version)] with no
        // DefaultDllImportSearchPaths. Libraries.Version is "version.dll".
        list.Add(new Import("version.dll", "System.Diagnostics.FileVersionInfo", singleFile, null, false,
            "GetFileVersionInfoSizeEx", "Canvas.Start syncs windows through FileVersionInfo.GetVersionInfo"));
    }
    if (text.Contains("WinVerifyTrust", StringComparison.Ordinal))
        AddProduct("wintrust.dll", "an elevated release verifies an update with WinVerifyTrust");
    return list
        .GroupBy(i => i.Owner + "\0" + i.Library + "\0" + i.Method)
        .Select(g => g.First())
        .ToList();
}

static bool RelaunchesThisExecutable(List<SyntaxTree> trees)
{
    bool runas = false, processPath = false, shell = false, setting = false;
    foreach (var tree in trees)
    {
        var root = tree.GetRoot();
        if (root.DescendantNodes().OfType<LiteralExpressionSyntax>().Any(l => l.Token.ValueText == "runas"))
            runas = true;
        if (root.ToString().Contains("Environment.ProcessPath", StringComparison.Ordinal)
            && root.ToString().Contains("lpFile", StringComparison.Ordinal))
            processPath = true;
        if (root.ToString().Contains("ShellExecuteEx", StringComparison.Ordinal))
            shell = true;
        if (root.ToString().Contains("RunAsAdmin", StringComparison.Ordinal) && root.ToString().Contains("StartElevated", StringComparison.Ordinal))
            setting = true;
    }
    return runas && processPath && shell && setting;
}

static List<Resolver> ResolversBeforeCanvas(List<SyntaxTree> trees)
{
    var found = new List<Resolver>();
    foreach (var tree in trees)
    {
        var root = tree.GetRoot();
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax member
                || member.Name.Identifier.Text != "SetDllImportResolver")
                continue;
            if (!OccursBeforeCanvas(tree, invocation))
                continue;
            var args = invocation.ArgumentList.Arguments;
            if (args.Count < 2)
                continue;
            string? typeName = TypeOfName(args[0].Expression);
            if (typeName == null)
                continue;
            bool safe = ResolverIsSystem32Only(root, args[1].Expression);
            found.Add(new Resolver(typeName, safe));
        }
    }
    return found;
}

static bool OccursBeforeCanvas(SyntaxTree tree, SyntaxNode node)
{
    string path = tree.FilePath.Replace('\\', '/');
    if (!path.EndsWith("src/Expanse/Program.cs", StringComparison.Ordinal))
        return false;
    int canvas = tree.ToString().IndexOf("new Canvas", StringComparison.Ordinal);
    return canvas < 0 || node.SpanStart < canvas;
}

static string? TypeOfName(ExpressionSyntax expression)
{
    // typeof(FileVersionInfo).Assembly or typeof(D2D1).Assembly
    if (expression is MemberAccessExpressionSyntax { Expression: TypeOfExpressionSyntax typeOf })
        return typeOf.Type.ToString().Split('.').Last();
    if (expression is TypeOfExpressionSyntax direct)
        return direct.Type.ToString().Split('.').Last();
    return null;
}

static bool ResolverIsSystem32Only(SyntaxNode root, ExpressionSyntax callback)
{
    string? name = callback switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
        _ => null,
    };
    if (name == null)
        return false;
    var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == name);
    if (method == null)
        return false;
    string body = method.ToString();
    bool systemDir = body.Contains("SystemDirectory", StringComparison.Ordinal) || body.Contains("GetSystemDirectory", StringComparison.Ordinal);
    bool system32Flag = body.Contains("System32", StringComparison.Ordinal) || body.Contains("0x800", StringComparison.Ordinal) || body.Contains("2048", StringComparison.Ordinal);
    bool usesAppDir = body.Contains("ProcessPath", StringComparison.Ordinal)
        || body.Contains("BaseDirectory", StringComparison.Ordinal)
        || body.Contains("GetDirectoryName", StringComparison.Ordinal);
    return systemDir && system32Flag && !usesAppDir;
}

// --- .NET 10 P/Invoke search, Windows half -------------------------------------------------------
// nativelibrary.cpp LoadNativeLibrary (resolver, then LoadNativeLibraryBySearch) and
// LoadNativeLibraryBySearch: native search directories, then the assembly directory when that
// flag is on, then LoadLibraryEx on the bare name. Defaults when the attribute is absent:
// search the assembly directory, and pass flags 0 (GetDefaultDllImportSearchPathFlags).

static Decision Resolve(Import import, string installDir, bool singleFile, List<Resolver> resolvers)
{
    if (resolvers.Any(r => r.System32Only && ResolverCovers(r, import)))
        return new Decision(null, "DllImportResolver loads System32 only");

    bool userSpecified = import.FlagsSpecified;
    bool searchAssemblyDirectory;
    uint dllFlags;
    if (userSpecified)
    {
        uint raw = import.SearchFlags ?? 0;
        searchAssemblyDirectory = (raw & AssemblyDirectoryBit) != 0;
        dllFlags = raw & ~AssemblyDirectoryBit;
    }
    else
    {
        searchAssemblyDirectory = true;
        dllFlags = 0;
    }

    // Single-file: assembly directory is the executable's directory (bundle path).
    // Side-by-side: Expanse's own assembly is still next to the exe. The shared framework's
    // FileVersionInfo assembly is not, unless this publish bundles it.
    string assemblyDir = import.Owner == "Expanse" || import.InBundle || (singleFile && import.Owner != "System.Diagnostics.FileVersionInfo")
        ? installDir
        : import.InBundle ? installDir : Path.Combine(installDir, "not-the-framework");
    if (import.Owner == "System.Diagnostics.FileVersionInfo")
        assemblyDir = import.InBundle ? installDir : Path.Combine(installDir, "not-the-framework");

    if (searchAssemblyDirectory)
    {
        string candidate = Path.Combine(assemblyDir, import.Library);
        if (File.Exists(candidate))
            return new Decision(candidate, "assembly directory, LoadLibraryEx full path with LOAD_WITH_ALTERED_SEARCH_PATH 0x" + LoadWithAlteredSearchPath.ToString("X"));
    }

    bool fallThrough = !userSpecified || !searchAssemblyDirectory || dllFlags != 0;
    if (!fallThrough)
        return new Decision(null, "no further probe");

    if (dllFlags == 0)
    {
        // LoadLibraryEx(name, flags 0): standard search, application directory then System32.
        string application = Path.Combine(installDir, import.Library);
        if (File.Exists(application))
            return new Decision(application, "standard search, application directory before System32");
        return new Decision(null, "standard search would continue to System32");
    }

    uint search = dllFlags & LoadLibrarySearchFlagMask;
    bool appDir = (search & (LoadLibrarySearchApplicationDir | LoadLibrarySearchDefaultDirs | LoadLibrarySearchDllLoadDir)) != 0;
    if (appDir && File.Exists(Path.Combine(installDir, import.Library)))
        return new Decision(Path.Combine(installDir, import.Library), "LOAD_LIBRARY_SEARCH flags 0x" + search.ToString("X") + " include the application directory");
    if ((search & LoadLibrarySearchSystem32) != 0 && !appDir)
        return new Decision(null, "LOAD_LIBRARY_SEARCH_SYSTEM32 only");
    return new Decision(null, "flags 0x" + search.ToString("X"));
}

static bool ResolverCovers(Resolver resolver, Import import) => import.Owner switch
{
    "System.Diagnostics.FileVersionInfo" => resolver.TypeName == "FileVersionInfo",
    "Vortice.Direct2D1" => resolver.TypeName is "D2D1" or "DWrite",
    "Expanse" => false,
    _ => false,
};

record Import(string Library, string Owner, bool InBundle, uint? SearchFlags, bool FlagsSpecified, string Method, string Why);

record Resolver(string TypeName, bool System32Only);

record Decision(string? ChosenPath, string How)
{
    public string Describe() => ChosenPath == null ? How : ChosenPath + " (" + How + ")";
}
