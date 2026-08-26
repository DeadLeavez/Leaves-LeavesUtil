using System.Reflection;
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace Leaves;

// LeavesUtils has no behavior of its own; this exists so SPT's mod loader treats the folder as a real mod instead of silently discarding it (which would drop all its [Injectable] classes).
public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "Leaves.You.Alone.LeavesUtils";
    public override string Name { get; init; } = "LeavesUtils";
    public override string Author { get; init; } = "DeadLeaves";
    public override List<string>? Contributors { get; init; } = new List<string> { "Leaves" };
    public override SemanticVersioning.Version Version { get; init; } = new(GetAssemblyVersion());
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.0");
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; } = false;
    public override string License { get; init; } = "MIT";

    private static string GetAssemblyVersion()
    {
        return Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "1.0.0";
    }
}
