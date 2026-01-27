using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Mod;
using StrmAssistant.Options.Store;
using StrmAssistant.Options.View;
using StrmAssistant.Web.Helper;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

#nullable disable
namespace StrmAssistant
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasUIPages, IServerEntryPoint, IHasThumbImage
    {
        private List<IPluginUIPageController> _pages;
        public readonly PluginOptionsStore MainOptionsStore;
        public static Plugin Instance { get; private set; }
        private bool _isDelayedRestoreTaskExecuted = false;

        private readonly Guid _id = new Guid("63c322b7-a371-41a3-b11f-04f8418b37d8");
        public string StrmToolConfigDirectoryPath { get; }
        public readonly ILogger Logger;
        public readonly IApplicationHost ApplicationHost;
        public new readonly IApplicationPaths ApplicationPaths;
        public readonly IServerConfigurationManager ConfigurationManager;
        public readonly ILibraryManager LibraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly ITaskManager _taskManager;
        private readonly IXmlSerializer _xmlSerializer;
        private readonly IItemRepository _itemRepository;

        public bool DebugMode;

        public Plugin(IApplicationHost applicationHost, IApplicationPaths applicationPaths, ILogManager logManager,
            IFileSystem fileSystem, IServerConfigurationManager configurationManager, ITaskManager taskManager,
            ILibraryManager libraryManager, IXmlSerializer xmlSerializer, IItemRepository itemRepository)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            Logger = logManager.GetLogger(Name);
            Logger.Info("★ StrmAssistant_less Start");
            ApplicationHost = applicationHost;
            ApplicationPaths = applicationPaths;
            ConfigurationManager = configurationManager;

            MainOptionsStore = new PluginOptionsStore(applicationHost, Logger, Name);

            LibraryManager = libraryManager;
            _fileSystem = fileSystem;
            _taskManager = taskManager;
            _xmlSerializer = xmlSerializer;
            _itemRepository = itemRepository;

            DefaultUICulture = new CultureInfo(configurationManager.Configuration.UICulture);
            DebugMode = true;
        }

        public static bool IsModSupported
        {
            get
            {
                var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                var isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
                var isOsx = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
                var arch = RuntimeInformation.ProcessArchitecture;

                if (arch == Architecture.X64) return true;
                if (isLinux && arch == Architecture.Arm64) return true;
                if (isOsx) return true;
                return false;
            }
        }

        public void Run() => Initialize();
        public void Dispose() { }

        private void Initialize()
        {
            ShortcutMenuHelper.Initialize();

            if (IsModSupported)
            {
                PatchManager.Initialize();

                var options = MainOptionsStore.GetOptions().ModOptions;
                if (options.EnhanceChineseSearch || options.EnhanceChineseSearchRestore)
                {
                    try
                    {
                        new EnhanceChineseSearch();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("初始化 EnhanceChineseSearch 失败", ex);
                        if (options.EnhanceChineseSearch)
                        {
                            options.EnhanceChineseSearch = false;
                            MainOptionsStore.SavePluginOptionsSuppress();
                        }
                    }
                }
            }
            else
            {
                Logger.Warn("当前平台/架构不支持增强功能: " +
                    $"OS={RuntimeInformation.OSDescription}, " +
                    $"Arch={RuntimeInformation.ProcessArchitecture}");
            }

        }

        public override void OnUninstalling()
        {
            if (MainOptionsStore.GetOptions().ModOptions.EnhanceChineseSearch)
            {
            }
            try
            {
                if (Directory.Exists(StrmToolConfigDirectoryPath) && Directory.GetFiles(StrmToolConfigDirectoryPath).Length == 0)
                {
                    Directory.Delete(StrmToolConfigDirectoryPath);
                    Logger.Info("StrmTool - Empty configuration directory deleted");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("StrmTool - Failed to delete configuration directory", ex);
            }
            base.OnUninstalling();
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;
        public override string Description => "★ 神医助手_less";
        public override Guid Id => _id;
        public sealed override string Name => "StrmAssistant_less";
        public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version;
        public CultureInfo DefaultUICulture { get; private set; }
        public Stream GetThumbImage()
        {
            var assembly = typeof(Plugin).Assembly;
            return assembly.GetManifestResourceStream("StrmAssistant.Properties.thumb.png");
        }



        public IReadOnlyCollection<IPluginUIPageController> UIPageControllers
        {
            get
            {
                if (_pages == null)
                {
                    PluginInfo basePluginInfo = base.GetPluginInfo();
                    _pages = new List<IPluginUIPageController>
                    {
                        new MainPageController(basePluginInfo, MainOptionsStore)
                    };
                }
                return _pages.AsReadOnly();
            }
        }

    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        public string StrmBackupPath { get; set; } = string.Empty;
        public bool AutoExtractMediaInfo { get; set; } = true;
        public bool EnableStrmBackup { get; set; } = false;
    }
}