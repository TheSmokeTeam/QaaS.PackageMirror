using System.Runtime.CompilerServices;

namespace QaaS.ElasticBootstrap;

internal static class QaaSElasticBootstrapModuleInitializer
{
    [ModuleInitializer]
    internal static void Initialize() => Bootstrap.Register();
}
