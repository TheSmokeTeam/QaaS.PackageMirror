using System.Runtime.CompilerServices;

namespace QaaS.Configuration;

internal static class QaaSConfigurationModuleInitializer
{
    [ModuleInitializer]
    internal static void Initialize() => ConfigurationBootstrap.Register();
}
