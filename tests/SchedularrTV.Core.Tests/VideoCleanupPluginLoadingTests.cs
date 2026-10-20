using System.Runtime.Loader;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SchedularrTV.GeneratedPromos;
using SchedularrTV.Plugins;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class VideoCleanupPluginLoadingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Installer_uses_host_plugin_directory_when_assembly_has_no_location(bool assetsPresent)
    {
        string root = Path.Combine(Path.GetTempPath(), "cleanup-plugin-loading-" + Guid.NewGuid().ToString("N"));
        var loading = new AssemblyLoadContext("cleanup-test-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            string directory = Path.Combine(root, "installed-plugin");
            Directory.CreateDirectory(Path.Combine(directory, "wwwroot"));
            if (assetsPresent)
            {
                File.WriteAllText(Path.Combine(directory, "wwwroot", "text-removal.py"), "# bundled worker");
                File.WriteAllText(Path.Combine(directory, "wwwroot", "text-removal-requirements.txt"), "# bundled requirements");
            }

            // Match PluginLoadContext: loading a DLL from a stream leaves Location empty.
            using var file = File.OpenRead(typeof(GeneratedPromosPlugin).Assembly.Location);
            var assembly = loading.LoadFromStream(file);
            Assert.Equal(string.Empty, assembly.Location);
            var plugin = (IPlugin)Activator.CreateInstance(assembly.GetType(typeof(GeneratedPromosPlugin).FullName!)!)!;
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GeneratedPromos:OutputPath"] = Path.Combine(root, "cache"),
                ["CleanupPython"] = Path.Combine(root, "deliberately-missing-python")
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            plugin.ConfigureServices(services, new Context(root, directory, config));
            using var provider = services.BuildServiceProvider();
            Type serviceType = assembly.GetType(typeof(PromoVideoCleanupService).FullName!)!;
            object service = provider.GetRequiredService(serviceType);
            await (Task)serviceType.GetMethod(nameof(PromoVideoCleanupService.InstallAsync))!.Invoke(service, null)!;
            string error = Assert.IsType<string>(serviceType.GetProperty(nameof(PromoVideoCleanupService.InstallError))!.GetValue(service));
            Assert.DoesNotContain("path1", error);
            if (assetsPresent)
                Assert.Contains("deliberately-missing-python", error);
            else
                Assert.Contains(Path.Combine(directory, "wwwroot", "text-removal.py"), error);
        }
        finally
        {
            loading.Unload();
            TestFolders.Delete(root);
        }
    }

    private sealed class Context(string root, string directory, IConfiguration configuration) : IPluginContext
    {
        public PluginManifest Manifest { get; } = new() { Id = "generated-promos", Name = "Generated promos", ApiVersion = PluginApi.Version.ToString() };
        public string PluginDirectory => directory;
        public string DataDirectory => Path.Combine(root, "plugin-data");
        public string AppDataDirectory => root;
        public IConfiguration Configuration => configuration;
        public IConfiguration AppConfiguration => configuration;
        public bool IsLoaded(string pluginId) => true;
        public string StaticFilesPath => "/plugins/generated-promos/";
    }
}
