using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

static class Installation
{
    const string ManifestName = "HostPause.install.json";
    public sealed class Entry
    {
        public string Name { get; set; } = "";
        public string? OriginalHash { get; set; }
        public string PatchedHash { get; set; } = "";
    }
    public sealed class Manifest
    {
        public string Version { get; set; } = "0.2.0";
        public string Backup { get; set; } = "";
        public List<Entry> Files { get; set; } = new();
    }
    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    static void Closed(string game)
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string name;
                try { name = process.ProcessName; } catch { continue; }
                if (name == "Terraria" || name.StartsWith("Terraria.bin", StringComparison.Ordinal) || name.StartsWith("TerrariaServer", StringComparison.Ordinal))
                {
                    string? executable;
                    try { executable = process.MainModule?.FileName; }
                    catch { throw new Exception("Не удалось проверить запущенную Terraria; закройте игру перед установкой."); }
                    if (executable != null && String.Equals(Path.GetDirectoryName(Path.GetFullPath(executable)), game.TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        throw new Exception("Сначала сохранитесь и полностью закройте Terraria и её сервер. Установка ничего не изменила.");
                }
            }
        }
    }
    public static string FindGame(string? supplied)
    {
        if (supplied != null)
        {
            string path = Path.GetFullPath(supplied);
            if (!File.Exists(Path.Combine(path, "Terraria.exe"))) throw new Exception("Terraria.exe не найден: " + path);
            return path;
        }
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            string? steam = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (steam != null) roots.Add(steam);
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        }
        else
        {
            roots.Add(Path.Combine(user, ".var/app/com.valvesoftware.Steam/.local/share/Steam"));
            roots.Add(Path.Combine(user, ".local/share/Steam"));
            roots.Add(Path.Combine(user, ".steam/steam"));
        }
        var libraries = new List<string>(roots);
        foreach (string root in roots)
        {
            string vdf = Path.Combine(root, "steamapps/libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (Match match in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
        }
        return libraries.Select(p => Path.Combine(p, "steamapps/common/Terraria"))
            .FirstOrDefault(p => File.Exists(Path.Combine(p, "Terraria.exe")))
            ?? throw new Exception("Не удалось найти Terraria. Передайте путь к папке игры вторым аргументом.");
    }
    public const string ConfigName = "HostPause.config.txt", LegacyKeyName = "HostPause.key.txt";
    public const string DefaultConfig = "# HostPause settings. Restart Terraria after editing.\n# key: pause key name, for example F9, F8 or Pause.\nkey=F9\n# countdown: seconds before the game resumes, 0-10 (0 = instantly).\ncountdown=3\n";
    static bool UserEditable(string name) => name == ConfigName || name == LegacyKeyName;
    static string? Version() => typeof(Installation).Assembly.GetName().Version?.ToString(3);

    static Manifest? ReadManifest(string game)
    {
        string path = Path.Combine(game, ManifestName);
        if (!File.Exists(path)) return null;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path)) ?? throw new IOException("Invalid install manifest.");
        // Steam's "move install folder" keeps the backups but invalidates the absolute path.
        string moved = Path.Combine(game, "HostPause.backups", Path.GetFileName(manifest.Backup.TrimEnd('/', '\\')));
        if (!Directory.Exists(manifest.Backup) && Directory.Exists(moved)) manifest.Backup = moved;
        return manifest;
    }

    // Steam updates and "verify integrity" replace the patched executables but keep
    // HostPause's own files and manifest. Patched files that no longer match mean the
    // patch is no longer active.
    static List<Entry> Replaced(string game, Manifest manifest) =>
        manifest.Files.Where(e => !UserEditable(e.Name) && e.OriginalHash != null)
            .Where(e => !File.Exists(Path.Combine(game, e.Name)) || Hash(Path.Combine(game, e.Name)) != e.PatchedHash).ToList();

    public static void Status(string game)
    {
        Console.WriteLine("Папка игры: " + game);
        var manifest = ReadManifest(game);
        if (manifest == null) { Console.WriteLine("HostPause не установлен."); return; }
        var replaced = Replaced(game, manifest);
        if (replaced.Count == 0) Console.WriteLine("Установлен HostPause " + manifest.Version + ", патч активен.");
        else Console.WriteLine("HostPause " + manifest.Version + " был установлен, но Steam заменил файлы игры (" + String.Join(", ", replaced.Select(e => e.Name)) +
            "): патч не работает. Запустите Install ещё раз, чтобы восстановить его.");
    }

    public static void Install(string game)
    {
        Closed(game);
        string manifestPath = Path.Combine(game, ManifestName);
        var existing = ReadManifest(game);
        string scratch = Path.Combine(Path.GetTempPath(), "HostPause-" + Guid.NewGuid().ToString("N"));
        try
        {
            string? inputs = null;
            if (existing != null)
            {
                // Patch the originals first, so a failure leaves the earlier installation untouched.
                inputs = Path.Combine(scratch, "originals");
                Directory.CreateDirectory(inputs);
                foreach (string name in new[] { "Terraria.exe", "TerrariaServer.exe" })
                {
                    var entry = existing.Files.FirstOrDefault(e => e.Name == name);
                    string path = Path.Combine(game, name);
                    if (!File.Exists(path)) continue;
                    bool stillPatched = entry?.OriginalHash != null && Hash(path) == entry.PatchedHash;
                    File.Copy(stillPatched ? Path.Combine(existing.Backup, name) : path, Path.Combine(inputs, name));
                    if (stillPatched && Hash(Path.Combine(inputs, name)) != entry!.OriginalHash) throw new IOException("Backup verification failed: " + name + ". Ничего не изменено.");
                }
            }
            string temporary = Path.Combine(scratch, "staged");
            Program.Prepare(game, temporary, inputs: inputs);
            Closed(game);
            if (existing != null)
            {
                // Installing over an earlier build upgrades it; after a Steam update it repairs the patch.
                var replaced = RemoveInstalled(game, existing);
                Console.WriteLine(replaced.Count == 0
                    ? "Удалена предыдущая установка HostPause " + existing.Version + "."
                    : "Steam заменил файлы игры после установки HostPause " + existing.Version + " (" + String.Join(", ", replaced.Select(e => e.Name)) + "); остатки старого патча удалены.");
            }
            string backup = Path.Combine(game, "HostPause.backups", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(backup);
            var manifest = new Manifest { Version = Version() ?? "0.2.0", Backup = backup };
            var names = Directory.GetFiles(temporary).Select(Path.GetFileName).Where(n => n != "patch-report.json").Cast<string>().ToList();
            // Keep the player's settings from a previous installation; carry over a 0.1.x key file.
            if (File.Exists(Path.Combine(game, ConfigName))) { names.Remove(ConfigName); Console.WriteLine("Сохранены ваши настройки: " + ConfigName); }
            bool migrated = false;
            if (names.Contains(ConfigName) && File.Exists(Path.Combine(game, LegacyKeyName)))
            {
                string legacyKey = File.ReadAllText(Path.Combine(game, LegacyKeyName)).Trim();
                if (legacyKey.Length > 0 && legacyKey.All(Char.IsLetterOrDigit))
                {
                    File.WriteAllText(Path.Combine(temporary, ConfigName), DefaultConfig.Replace("key=F9", "key=" + legacyKey));
                    migrated = true;
                }
            }
            foreach (string name in names)
            {
                string path = Path.Combine(game, name);
                var entry = new Entry { Name = name, PatchedHash = Hash(Path.Combine(temporary, name)) };
                if (File.Exists(path))
                {
                    entry.OriginalHash = Hash(path);
                    File.Copy(path, Path.Combine(backup, name));
                    if (Hash(Path.Combine(backup, name)) != entry.OriginalHash) throw new IOException("Backup verification failed: " + name);
                }
                manifest.Files.Add(entry);
            }
            string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(backup, ManifestName), json);
            // Verify that neither Steam nor another process changed inputs after staging.
            foreach (Entry entry in manifest.Files)
            {
                string path = Path.Combine(game, entry.Name);
                if ((entry.OriginalHash == null && File.Exists(path)) || (entry.OriginalHash != null && Hash(path) != entry.OriginalHash))
                    throw new IOException("Файл изменился во время подготовки: " + entry.Name);
            }
            try
            {
                foreach (Entry entry in manifest.Files) Replace(Path.Combine(temporary, entry.Name), Path.Combine(game, entry.Name));
                foreach (Entry entry in manifest.Files)
                    if (Hash(Path.Combine(game, entry.Name)) != entry.PatchedHash) throw new IOException("Post-install verification failed.");
                File.WriteAllText(manifestPath, json);
                // The 0.1.x key now lives in the config; the old file would only confuse.
                if (migrated) { File.Delete(Path.Combine(game, LegacyKeyName)); Console.WriteLine("Клавиша из " + LegacyKeyName + " перенесена в " + ConfigName + "."); }
            }
            catch
            {
                Restore(game, manifest);
                File.Delete(manifestPath);
                throw;
            }
            Console.WriteLine("Установлен HostPause " + manifest.Version + ". Клавиша паузы и отсчёт настраиваются в " + ConfigName + ".\nПапка игры: " + game + "\nРезервная копия: " + backup);
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }

    public static void Uninstall(string game)
    {
        Closed(game);
        var manifest = ReadManifest(game) ?? throw new Exception("HostPause не установлен в " + game);
        var replaced = RemoveInstalled(game, manifest);
        if (replaced.Count == 0) Console.WriteLine("HostPause удалён. Исходные файлы игры восстановлены; резервная копия сохранена.");
        else Console.WriteLine("HostPause удалён. Steam уже заменил " + String.Join(", ", replaced.Select(e => e.Name)) + " — эти файлы оставлены как есть; остальное восстановлено.");
    }

    // Undo one installation as far as its files are still HostPause's own:
    // still-patched executables get their backups back, added files are deleted,
    // files Steam replaced and settings the player edited are left alone.
    // Returns the executables Steam had already replaced.
    static List<Entry> RemoveInstalled(string game, Manifest manifest)
    {
        var replaced = Replaced(game, manifest);
        bool Ours(Entry e) { string path = Path.Combine(game, e.Name); return File.Exists(path) && Hash(path) == e.PatchedHash; }
        foreach (Entry entry in manifest.Files.Where(e => e.OriginalHash != null && Ours(e)))
            if (Hash(Path.Combine(manifest.Backup, entry.Name)) != entry.OriginalHash)
                throw new IOException("Backup verification failed: " + entry.Name + ". Ничего не изменено.");
        foreach (Entry entry in manifest.Files)
        {
            string path = Path.Combine(game, entry.Name);
            if (UserEditable(entry.Name) && !Ours(entry)) continue;
            if (entry.OriginalHash != null) { if (Ours(entry)) Replace(Path.Combine(manifest.Backup, entry.Name), path); }
            else if (File.Exists(path)) File.Delete(path);
        }
        string manifestPath = Path.Combine(game, ManifestName);
        if (replaced.Count == 0) File.Delete(manifestPath);
        else File.Move(manifestPath, Path.Combine(Directory.Exists(manifest.Backup) ? manifest.Backup : game, "HostPause.install.replaced-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json"), true);
        return replaced;
    }

    // Rollback of a failed installation: put back exactly what was there before it.
    static void Restore(string game, Manifest manifest)
    {
        foreach (Entry entry in manifest.Files)
        {
            string path = Path.Combine(game, entry.Name);
            if (entry.OriginalHash == null) File.Delete(path);
            else Replace(Path.Combine(manifest.Backup, entry.Name), path);
        }
    }

    static void Replace(string source, string target)
    {
        string pending = target + ".hostpause-new";
        try
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write)) { input.CopyTo(output); output.Flush(true); }
            if (!OperatingSystem.IsWindows() && File.Exists(target)) File.SetUnixFileMode(pending, File.GetUnixFileMode(target));
            File.Move(pending, target, true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
}
