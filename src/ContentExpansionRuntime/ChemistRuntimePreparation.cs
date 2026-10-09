using System.Reflection;
using System.Runtime.CompilerServices;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>Prepare our closed IL methods before activating native detours.
/// This reduces first-use JIT in gameplay; it does NOT repair CLR corruption
/// or promise that framework/open-generic methods will never compile later.</summary>
public static class ChemistRuntimePreparation
{
    public static int Prepare(Action<string> log)
    {
        int prepared=0,open=0;
        foreach(var type in typeof(ChemistPlayableHost).Assembly.GetTypes()
            .Where(t=>t.Namespace?.StartsWith("FFTModLoader.ContentExpansion",StringComparison.Ordinal)==true))
        {
            const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance|BindingFlags.DeclaredOnly;
            IEnumerable<MethodBase> methods=type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags));
            foreach(var method in methods)
            {
                if(type.ContainsGenericParameters||method.ContainsGenericParameters){open++;continue;}
                if(method.IsAbstract||(method.Attributes&MethodAttributes.PinvokeImpl)!=0||method.GetMethodBody() is null)continue;
                // Compiles but does not call the method or its constructor.
                // No observer/game helper is executed during preparation.
                RuntimeHelpers.PrepareMethod(method.MethodHandle);prepared++;
            }
        }
        object? tiered=AppContext.GetData("System.Runtime.TieredCompilation");
        bool configured=AppContext.TryGetSwitch("System.Runtime.TieredCompilation",out bool enabled);
        log($"[Runtime] .NET={Environment.Version}; TieredCompilation switch={(configured?enabled.ToString():"indisponível")}; propriedade={tiered??"indisponível"}; métodos Chemist preparados={prepared}; membros genéricos abertos não preparados={open}. Sem garantia de eliminar JIT do framework.");
        return prepared;
    }
}
