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
        public string Version { get; set; } = "0.1.2";
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
    public static void Install(string game)
    {
        Closed(game);
        string manifestPath = Path.Combine(game, ManifestName);
        if (File.Exists(manifestPath)) throw new Exception("HostPause уже установлен. Сначала выполните uninstall.");
        string temporary = Path.Combine(Path.GetTempPath(), "HostPause-" + Guid.NewGuid().ToString("N"));
        try
        {
            Program.Prepare(game, temporary);
            Closed(game);
            string backup = Path.Combine(game, "HostPause.backups", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(backup);
            var manifest = new Manifest { Backup = backup };
            var names = Directory.GetFiles(temporary).Select(Path.GetFileName).Where(n => n != "patch-report.json").Cast<string>().ToList();
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
            }
            catch
            {
                Restore(game, manifest);
                File.Delete(manifestPath);
                throw;
            }
            Console.WriteLine("Установлен HostPause 0.1.2. Пауза хоста: F9.\nПапка игры: " + game + "\nРезервная копия: " + backup);
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }
    public static void Uninstall(string game)
    {
        Closed(game);
        string manifestPath = Path.Combine(game, ManifestName);
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath)) ?? throw new IOException("Invalid install manifest.");
        foreach (Entry entry in manifest.Files)
        {
            string path = Path.Combine(game, entry.Name);
            if (entry.Name != "HostPause.key.txt" && (!File.Exists(path) || Hash(path) != entry.PatchedHash))
                throw new IOException("Файл обновлён после установки: " + entry.Name + ". Старую версию автоматически не восстанавливаю.");
            if (entry.OriginalHash != null && Hash(Path.Combine(manifest.Backup, entry.Name)) != entry.OriginalHash)
                throw new IOException("Backup verification failed: " + entry.Name);
        }
        Restore(game, manifest);
        File.Delete(manifestPath);
        Console.WriteLine("HostPause удалён. Исходные файлы игры восстановлены; резервная копия сохранена.");
    }
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
