using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
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
using System.Reflection;
using System.Runtime.InteropServices;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant
{
    public class Plugin : BasePlugin, IHasUIPages
    {
        private List<IPluginUIPageController> _pages;
        public readonly PluginOptionsStore MainOptionsStore;

        public static Plugin Instance { get; private set; }

        private readonly Guid _id = new Guid("63c322b7-a371-41a3-b11f-04f8418b37d8");

        public readonly ILogger Logger;
        public readonly IApplicationHost ApplicationHost;
        public readonly IApplicationPaths ApplicationPaths;

        private readonly IFileSystem _fileSystem;
        private readonly ITaskManager _taskManager;

        public Plugin(IApplicationHost applicationHost, IApplicationPaths applicationPaths, ILogManager logManager,
            IFileSystem fileSystem, INotificationManager notificationManager, IJsonSerializer jsonSerializer,
            IHttpClient httpClient, IServerApplicationHost serverApplicationHost,
            IServerConfigurationManager configurationManager, ITaskManager taskManager,
            IServerApplicationPaths serverApplicationPaths)
        {
            Instance = this;
            Logger = logManager.GetLogger(Name);
            Logger.Info("StrmAssistant_less Plugin is getting loaded.");
            ApplicationHost = applicationHost;
            ApplicationPaths = applicationPaths;

            _fileSystem = fileSystem;
            _taskManager = taskManager;

            MainOptionsStore = new PluginOptionsStore(applicationHost, Logger, Name);
            InitializeOptionCache();

            if (MainOptionsStore.GetOptions().AboutOptions.DebugMode)
            {
                DebugMode = true;
                MainOptionsStore.GetOptions().AboutOptions.DebugMode = false;
                MainOptionsStore.SavePluginOptionsSuppress();
            }
            else if (Debugger.IsAttached)
            {
                DebugMode = true;
            }

            // Ä£ºýËÑË÷
            if (IsModSupported) PatchManager.Initialize();
            // Ä£ºýËÑË÷

            ShortcutMenuHelper.Initialize(configurationManager);

        }
        // Ä£ºýËÑË÷
        public override void OnUninstalling()
        {
            if (MainOptionsStore.GetOptions().ModOptions.EnhanceChineseSearch)
            {
                //_ = NotificationApi.SendMessageToAdmins(
                //    $"[{Resources.PluginOptions_EditorTitle_Strm_Assistant}] {Resources.Uninstall_Warning}", 10000);
            }

            base.OnUninstalling();
        }
        // Ä£ºýËÑË÷

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public override string Description => "Extract MediaInfo and Enable IntroSkip";

        public override Guid Id => _id;

        public sealed override string Name => "Strm Assistant";

        public string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString();

        public string UserAgent => $"{Name}/{CurrentVersion}";

        public CultureInfo DefaultUICulture =>
            new CultureInfo(MainOptionsStore.GetOptions().AboutOptions.DefaultUICulture);

        public bool DebugMode = true;

        public bool IsModSupported => RuntimeInformation.ProcessArchitecture == Architecture.X64;


        public IReadOnlyCollection<IPluginUIPageController> UIPageControllers
        {
            get
            {
                if (_pages == null)
                {
                    _pages = new List<IPluginUIPageController>
                    {
                        new MainPageController(GetPluginInfo(), MainOptionsStore)
                    };
                }

                return _pages.AsReadOnly();
            }
        }
    }
}
