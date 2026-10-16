using Mono.Cecil;
using Mono.Cecil.Cil;
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
using var module = ModuleDefinition.ReadModule(args[0], new ReaderParameters { AssemblyResolver = resolver });
using var audit = ModuleDefinition.ReadModule(args[1]);
var update = module.GetType("Terraria.Main").Methods.Single(m => m.Name == "DoUpdate");
var render = audit.GetType("HostPauseRenderClient");
if (render == null)
{
    var observe = module.ImportReference(audit.GetType("HostPauseAudit").Methods.Single(m => m.Name == "Observe"));
    update.Body.GetILProcessor().InsertBefore(update.Body.Instructions[0], Instruction.Create(OpCodes.Call, observe));
}
else
{
    MethodReference Hook(string name) => module.ImportReference(render.Methods.Single(m => m.Name == name));
    var il = update.Body.GetILProcessor();
    il.InsertBefore(update.Body.Instructions[0], Instruction.Create(OpCodes.Call, Hook("OnFrame")));
    var gate = update.Body.Instructions.Single(i => i.Operand is MethodReference r && r.DeclaringType.FullName == "HostPause.Runtime" && r.Name == "BeforeSimulation");
    il.InsertBefore(gate, Instruction.Create(OpCodes.Call, Hook("DriveKey")));
    var draw = module.GetType("Terraria.Main").Methods.Single(m => m.Name == "Draw" && m.Parameters.Count == 1);
    draw.Body.GetILProcessor().InsertBefore(draw.Body.Instructions.Last(), Instruction.Create(OpCodes.Call, Hook("OnDraw")));
    var launch = module.GetType("Terraria.Program").Methods.Single(m => m.Name == "RunGame");
    launch.Body.GetILProcessor().InsertBefore(launch.Body.Instructions[0], Instruction.Create(OpCodes.Call, Hook("BeforeGame")));
    var social = launch.Body.Instructions.Single(i => i.Operand is MethodReference r && r.DeclaringType.FullName == "Terraria.Social.SocialAPI" && r.Name == "Initialize");
    social.Operand = Hook("InitSocialWithoutSteam");
    var overlay = draw.Body.Instructions.Single(i => i.Operand is MethodReference r && r.DeclaringType.FullName == "HostPause.Runtime" && r.Name == "AfterDraw");
    draw.Body.GetILProcessor().InsertBefore(overlay, Instruction.Create(OpCodes.Call, Hook("OnOverlay")));
}
module.Write(args[0] + ".audit");
File.Move(args[0] + ".audit", args[0], true);
