using Mono.Cecil;
using System.Security.Cryptography;
using System.Text.Json;

static class ResolverRegression
{
    static readonly List<string> checks = new();
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        checks.Add(name);
        Console.WriteLine("PASS " + name);
    }
    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    static void Main(string[] args)
    {
        // Supply private fixture directories: no game files or dependencies are redistributed.
        string windowsGame = Path.GetFullPath(args[0]);
        string windows = Path.GetFullPath(args[1]);
        string nativeOriginals = Path.GetFullPath(args[2]);
        string output = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(output);
        var reference = AssemblyNameReference.Parse("Microsoft.Xna.Framework.Graphics, Version=4.0.0.0, Culture=neutral, PublicKeyToken=842cf8be1de50553");
        using (var old = new DefaultAssemblyResolver())
        {
            old.AddSearchDirectory(windowsGame);
            bool failed = false;
            try { old.Resolve(reference); } catch (AssemblyResolutionException) { failed = true; }
            Check(failed, "Original .NET 8 resolver reproduces missing XNA Graphics");
        }
        using (var fixedResolver = new GameAssemblyResolver(windowsGame, windows))
        {
            Check(fixedResolver.Resolve(reference).Name.FullName == reference.FullName,
                "Resolve XNA Graphics by exact identity from Windows GAC_32");
            var wrongVersion = AssemblyNameReference.Parse(reference.FullName);
            wrongVersion.Version = new Version(9, 0, 0, 0);
            bool failed = false;
            try { fixedResolver.Resolve(wrongVersion); } catch (AssemblyResolutionException) { failed = true; }
            Check(failed, "Do not substitute another XNA version");
        }
        // Check the legacy GAC layout independently of the modern GAC_32 tree.
        string legacy = Path.Combine(output, "legacy-windows");
        string source = Path.Combine(windows, "Microsoft.NET", "assembly", "GAC_32", reference.Name,
            "v4.0_4.0.0.0__842cf8be1de50553", reference.Name + ".dll");
        string target = Path.Combine(legacy, "assembly", "GAC_MSIL", reference.Name,
            "4.0.0.0__842cf8be1de50553", reference.Name + ".dll");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target);
        using (var resolver = new GameAssemblyResolver(windowsGame, legacy))
            Check(resolver.Resolve(reference).Name.FullName == reference.FullName,
                "Resolve XNA from the legacy Windows assembly GAC_MSIL layout");

        string mismatched = Path.Combine(output, "mismatched-windows");
        string wrongFile = Path.Combine(mismatched, "assembly", "GAC_MSIL", reference.Name,
            "4.0.0.0__842cf8be1de50553", reference.Name + ".dll");
        Directory.CreateDirectory(Path.GetDirectoryName(wrongFile)!);
        File.Copy(Path.Combine(windows, "Microsoft.NET", "Framework", "v4.0.30319", "mscorlib.dll"), wrongFile);
        using (var resolver = new GameAssemblyResolver(windowsGame, mismatched))
        {
            bool failed = false;
            try { resolver.Resolve(reference); } catch (AssemblyResolutionException) { failed = true; }
            Check(failed, "Reject a DLL with a mismatched identity even at the expected GAC path");
        }

        string originalServer = Path.Combine(windowsGame, "TerrariaServer.exe");
        string serverHash = Hash(originalServer);
        string patchedWindows = Path.Combine(output, "windows-patched");
        Program.Prepare(windowsGame, patchedWindows, windows);
        Check(File.Exists(Path.Combine(patchedWindows, "TerrariaServer.exe")),
            "Patch official Windows Terraria 1.4.5.8 server using simulated Windows GAC");
        Check(Hash(originalServer) == serverHash, "Windows source executable is unchanged");
        using (var assembly = ModuleDefinition.ReadModule(Path.Combine(patchedWindows, "TerrariaServer.exe")))
            Check(assembly.AssemblyReferences.Any(a => a.Name == "HostPause.Runtime"),
                "Patched Windows server references HostPause runtime");

        string patchedNative = Path.Combine(output, "linux-patched");
        Program.Prepare(nativeOriginals, patchedNative);
        Check(Hash(Path.Combine(patchedNative, "Terraria.exe")) == "31e65ec445a1de65d4f9b5e2485ffb6fb9af2d4fb9a7b87dfb42387317667844",
            "Linux client patch remains byte-identical to the verified 0.1.0 output");
        Check(Hash(Path.Combine(patchedNative, "TerrariaServer.exe")) == "d0e3bfc29e14a34a436348d79108cb1d40b24e685a5078ac2a341826895fb9ba",
            "Linux server patch remains byte-identical to the verified 0.1.0 output");
        File.WriteAllText(Path.Combine(output, "resolver-result.json"), JsonSerializer.Serialize(new {
            passed = checks.Count, checks, windows_server = "official Terraria 1.4.5.8 download",
            environment = "Linux .NET 8; simulated Windows GAC using Wine Mono XNA facades",
            windows_gameplay_tested = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
