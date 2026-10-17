using Mono.Cecil;

// The netstandard Cecil build used by .NET 8 does not probe the .NET Framework
// GAC. Windows Terraria keeps its XNA dependencies there, outside the game folder.
sealed class GameAssemblyResolver : DefaultAssemblyResolver
{
    readonly string? windows;

    public GameAssemblyResolver(string game, string? windowsDirectory = null)
    {
        windows = windowsDirectory ?? (OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) : null);
        foreach (string directory in GetSearchDirectories()) RemoveSearchDirectory(directory);
        AddSearchDirectory(game);
        AddSearchDirectory(AppContext.BaseDirectory);
        if (!string.IsNullOrEmpty(windows))
        {
            foreach (string framework in new[] { "Framework", "Framework64" })
            {
                string directory = Path.Combine(windows, "Microsoft.NET", framework, "v4.0.30319");
                if (Directory.Exists(directory)) AddSearchDirectory(directory);
            }
            ResolveFailure += ResolveFromGac;
        }
    }

    AssemblyDefinition? ResolveFromGac(object sender, AssemblyNameReference reference)
    {
        if (reference.PublicKeyToken == null || reference.PublicKeyToken.Length == 0)
            return null;
        // Reference identities are read from game metadata, never used as arbitrary paths.
        if (reference.Name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return null;
        string token = Convert.ToHexString(reference.PublicKeyToken).ToLowerInvariant();
        string culture = string.IsNullOrEmpty(reference.Culture) || reference.Culture == "neutral"
            ? "" : reference.Culture;
        if (culture.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return null;
        string identity = reference.Version + "_" + culture + "_" + token;
        foreach (string root in new[] { Path.Combine(windows!, "assembly"), Path.Combine(windows!, "Microsoft.NET", "assembly") })
        foreach (string architecture in new[] { "GAC_32", "GAC_MSIL", "GAC_64", "GAC" })
        foreach (string prefix in new[] { "", "v4.0_" })
        {
            string path = Path.Combine(root, architecture, reference.Name, prefix + identity, reference.Name + ".dll");
            if (!File.Exists(path)) continue;
            var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = this });
            if (assembly.Name.FullName == reference.FullName) return assembly;
            assembly.Dispose();
        }
        return null;
    }
}
