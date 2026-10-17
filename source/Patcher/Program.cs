using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.Cryptography;
using System.Text.Json;

static class Program
{
    static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 1 && (args[0] == "install" || args[0] == "uninstall"))
            {
                string game = Installation.FindGame(args.Length > 1 ? args[1] : null);
                if (args[0] == "install") Installation.Install(game); else Installation.Uninstall(game);
                return 0;
            }
            if (args.Length != 3 || args[0] != "prepare")
                throw new ArgumentException("Usage: HostPause.Patcher install|uninstall [Terraria directory]\n       HostPause.Patcher prepare <Terraria directory> <output directory>");
            Prepare(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
            return 0;
        }
        catch (AssemblyResolutionException ex) when (ex.AssemblyReference.Name.StartsWith("Microsoft.Xna.Framework", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Не найдена сборка XNA: " + ex.AssemblyReference.FullName +
                "\nПроверены папка игры и системное хранилище сборок Windows. Если сама Terraria тоже не запускается, восстановите компоненты XNA 4.0 через Steam.");
            return 1;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    internal static void Prepare(string game, string output, string? windowsDirectory = null)
    {
        if (game.TrimEnd(Path.DirectorySeparatorChar) == output.TrimEnd(Path.DirectorySeparatorChar))
            throw new Exception("Use a separate staging directory; original files are never patched in place.");
        Directory.CreateDirectory(output);
        string runtimePath = Path.Combine(AppContext.BaseDirectory, "HostPause.Runtime.dll");
        using var runtime = ModuleDefinition.ReadModule(runtimePath);
        var type = runtime.GetType("HostPause.Runtime");
        using var resolver = new GameAssemblyResolver(game, windowsDirectory);
        var report = new List<object>();
        foreach (string name in new[] { "Terraria.exe", "TerrariaServer.exe" })
        {
            string input = Path.Combine(game, name), destination = Path.Combine(output, name);
            if (!File.Exists(input)) continue;
            if (File.Exists(destination)) throw new Exception("Output exists: " + destination);
            using var module = ModuleDefinition.ReadModule(input, new ReaderParameters { AssemblyResolver = resolver });
            if (module.Assembly.Name.Version != new Version(1, 4, 5, 8))
                throw new Exception("Only Terraria 1.4.5.8 is supported; found " + module.Assembly.Name.Version);
            if (module.AssemblyReferences.Any(r => r.Name == "HostPause.Runtime"))
                throw new Exception(name + " is already patched. Start from the original backup.");
            var main = module.GetType("Terraria.Main");
            var update = main.Methods.Single(m => m.Name == "DoUpdate" && m.Parameters.Count == 1);
            var before = module.ImportReference(type.Methods.Single(m => m.Name == "BeforeSimulation"));
            var frame = module.ImportReference(type.Methods.Single(m => m.Name == "OnFrame"));
            var handle = module.ImportReference(type.Methods.Single(m => m.Name == "HandlePacket"));

            // Hook only this precise simulation boundary. Abort instead of guessing on another game build.
            var weatherCalls = update.Body.Instructions.Where(i => i.Operand is MethodReference r && r.DeclaringType.FullName == "Terraria.Main" && r.Name == "UpdateWindyDayState").ToList();
            if (weatherCalls.Count != 1 || weatherCalls[0].Previous.OpCode != OpCodes.Ldarg_0)
                throw new Exception("Simulation boundary does not match the verified 1.4.5.8 layout.");
            var boundary = weatherCalls[0].Previous;
            if (update.Body.ExceptionHandlers.Any(h => InRange(boundary, h.TryStart, h.TryEnd) || InRange(boundary, h.HandlerStart, h.HandlerEnd)))
                throw new Exception("Cannot insert a return inside an exception region.");
            var networkIndex = update.Body.Instructions.ToList().FindIndex(i => i.Operand is MethodReference r && r.DeclaringType.FullName == "Terraria.Netplay" && r.Name == "UpdateInMainThread");
            if (networkIndex < 0 || networkIndex >= update.Body.Instructions.IndexOf(boundary))
                throw new Exception("Networking must run before the pause boundary.");
            var il = update.Body.GetILProcessor();
            var gate = Instruction.Create(OpCodes.Ldarg_0);
            // Server code jumps directly over the client-only input block to this boundary.
            // Redirect those existing branches through the gate, or only clients would freeze.
            foreach (var instruction in update.Body.Instructions)
            {
                if (instruction.Operand == boundary) instruction.Operand = gate;
                if (instruction.Operand is Instruction[] targets)
                    for (int i = 0; i < targets.Length; i++) if (targets[i] == boundary) targets[i] = gate;
            }
            InsertBefore(il, boundary, gate, Instruction.Create(OpCodes.Call, before), Instruction.Create(OpCodes.Brfalse, boundary), Instruction.Create(OpCodes.Ret));
            var first = update.Body.Instructions[0];
            InsertBefore(il, first, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, frame));

            var getData = module.GetType("Terraria.MessageBuffer").Methods.Single(m => m.Name == "GetData" && m.Parameters.Count == 3);
            il = getData.Body.GetILProcessor();
            first = getData.Body.Instructions[0];
            InsertBefore(il, first,
                Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Ldarg_2), Instruction.Create(OpCodes.Ldarg_3),
                Instruction.Create(OpCodes.Call, handle), Instruction.Create(OpCodes.Brfalse, first), Instruction.Create(OpCodes.Ret));

            var draw = main.Methods.SingleOrDefault(m => m.Name == "Draw" && m.Parameters.Count == 1);
            if (draw != null)
            {
                var doDraw = main.Methods.Single(m => m.Name == "DoDraw" && m.Parameters.Count == 1);
                var originalDraw = draw.Body.Instructions.Single(i => i.Operand is MethodReference r && r.Name == "DoDraw");
                draw.Body.GetILProcessor().InsertAfter(originalDraw, Instruction.Create(OpCodes.Call, module.ImportReference(type.Methods.Single(m => m.Name == "AfterDraw"))));
                var originalFirst = doDraw.Body.Instructions[0];
                InsertBefore(doDraw.Body.GetILProcessor(), originalFirst,
                    Instruction.Create(OpCodes.Call, module.ImportReference(type.Methods.Single(m => m.Name == "DrawFrozenFrame"))),
                    Instruction.Create(OpCodes.Brfalse, originalFirst), Instruction.Create(OpCodes.Ret));
            }

            var f9 = main.Methods.Single(m => m.Name == "DoUpdate_F9_ToggleLighting");
            var f9First = f9.Body.Instructions[0];
            InsertBefore(f9.Body.GetILProcessor(), f9First,
                Instruction.Create(OpCodes.Call, module.ImportReference(type.Methods.Single(m => m.Name == "SuppressF9"))),
                Instruction.Create(OpCodes.Brfalse, f9First), Instruction.Create(OpCodes.Ret));

            // New instructions can push old short branches beyond sbyte range.
            foreach (var method in new[] { update, getData, draw }.Where(m => m != null)) ExpandBranches(method!);
            module.Write(destination);
            using var verify = ModuleDefinition.ReadModule(destination);
            int hooks = verify.GetType("Terraria.Main").Methods.SelectMany(m => m.HasBody ? m.Body.Instructions : Enumerable.Empty<Instruction>())
                .Concat(verify.GetType("Terraria.MessageBuffer").Methods.SelectMany(m => m.HasBody ? m.Body.Instructions : Enumerable.Empty<Instruction>()))
                .Count(i => i.Operand is MethodReference r && r.DeclaringType.FullName == "HostPause.Runtime");
            if (hooks != (draw != null ? 6 : 4)) throw new Exception("Hook verification failed.");
            report.Add(new { file = name, version = module.Assembly.Name.Version.ToString(), originalSha256 = Hash(input), patchedSha256 = Hash(destination), hooks });
        }
        if (report.Count == 0) throw new Exception("No Terraria executables found.");
        File.Copy(runtimePath, Path.Combine(output, "HostPause.Runtime.dll"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "HostPause.banner.png"), Path.Combine(output, "HostPause.banner.png"));
        File.WriteAllText(Path.Combine(output, "HostPause.key.txt"), "F9\n");
        File.WriteAllText(Path.Combine(output, "patch-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    static bool InRange(Instruction target, Instruction? start, Instruction? end) => start != null && target.Offset >= start.Offset && (end == null || target.Offset < end.Offset);
    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    static void InsertBefore(ILProcessor il, Instruction target, params Instruction[] instructions) { foreach (var instruction in instructions) il.InsertBefore(target, instruction); }
    static void ExpandBranches(MethodDefinition method)
    {
        var shortCodes = new[] { OpCodes.Br_S, OpCodes.Brfalse_S, OpCodes.Brtrue_S, OpCodes.Beq_S, OpCodes.Bge_S, OpCodes.Bgt_S, OpCodes.Ble_S, OpCodes.Blt_S, OpCodes.Bne_Un_S, OpCodes.Bge_Un_S, OpCodes.Bgt_Un_S, OpCodes.Ble_Un_S, OpCodes.Blt_Un_S, OpCodes.Leave_S };
        var longCodes = new[] { OpCodes.Br, OpCodes.Brfalse, OpCodes.Brtrue, OpCodes.Beq, OpCodes.Bge, OpCodes.Bgt, OpCodes.Ble, OpCodes.Blt, OpCodes.Bne_Un, OpCodes.Bge_Un, OpCodes.Bgt_Un, OpCodes.Ble_Un, OpCodes.Blt_Un, OpCodes.Leave };
        foreach (var instruction in method.Body.Instructions)
        {
            int index = Array.IndexOf(shortCodes, instruction.OpCode);
            if (index >= 0) instruction.OpCode = longCodes[index];
        }
    }
}
