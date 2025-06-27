using MediaBrowser.Controller.Configuration;
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Web.Helper
{
    internal static class ShortcutMenuHelper
    {
        public static string ModifiedShortcutsString { get; private set; }

        public static MemoryStream StrmAssistantJs { get; private set; }

        public static void Initialize(IServerConfigurationManager configurationManager)
        {
            try
            {
                StrmAssistantJs = GetResourceStream("strmassistant.js");
                ModifyShortcutMenu(configurationManager);
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Error($"{nameof(ShortcutMenuHelper)} Init Failed");
                Plugin.Instance.Logger.Error(e.Message);
                Plugin.Instance.Logger.Info(e.StackTrace);
            }
        }

        private static MemoryStream GetResourceStream(string resourceName)
        {
            var name = typeof(Plugin).Namespace + ".Web.Resources." + resourceName;
            var manifestResourceStream = typeof (ShortcutMenuHelper).GetTypeInfo().Assembly.GetManifestResourceStream(name);
            var destination = new MemoryStream((int) manifestResourceStream.Length);
            manifestResourceStream.CopyTo((Stream) destination);
            return destination;
        }

        private static void ModifyShortcutMenu(IServerConfigurationManager configurationManager)
        {
            var dashboardSourcePath = configurationManager.Configuration.DashboardSourcePath ??
                                      Path.Combine(configurationManager.ApplicationPaths.ApplicationResourcesPath,
                                          "dashboard-ui");

            const string injectShortcutCommand = @"
const strmAssistantCommandSource = {
    getCommands: function(options) {
        const locale = this.globalize.getCurrentLocale().toLowerCase();
        const cjk = ['zh', 'ja', 'ko'].some(lang => locale.startsWith(lang));
        const lockCommandName = ({
            'zh-cn': '\u9501\u5B9A',
            'zh-hk': '\u9396\u5B9A',
            'zh-tw': '\u9396\u5B9A'
        }[locale] || 'Lock') + (cjk ? this.globalize.translate('Metadata') : ' ' + this.globalize.translate('Metadata'));
        const unlockCommandName = ({
            'zh-cn': '\u89E3\u9501',
            'zh-hk': '\u89E3\u9396',
            'zh-tw': '\u89E3\u9396'
        }[locale] || 'Unlock') + (cjk ? this.globalize.translate('Metadata') : ' ' + this.globalize.translate('Metadata'));

        if (options.items?.length === 1 && options.items[0].LibraryOptions && options.items[0].Type === 'VirtualFolder' &&
            options.items[0].CollectionType !== 'boxsets' && options.items[0].CollectionType !== 'playlists') {
            const commandName = (locale === 'zh-cn') ? '\u590D\u5236' : (['zh-hk', 'zh-tw'].includes(locale) ? '\u8907\u8F38' : 'Copy');
            return [{ name: commandName, id: 'copy', icon: 'content_copy' }];
        }
        if (options.items?.length === 1 && options.items[0].LibraryOptions && options.items[0].Type === 'VirtualFolder' &&
            options.items[0].CollectionType === 'boxsets') {
            return [{ name: this.globalize.translate('Remove'), id: 'remove', icon: 'remove_circle_outline' }];
        }
        if (options.items?.length === 1) {
            const result = [];
            if (options.items[0].Type === 'Movie') {
                result.push({ name: this.globalize.translate('HeaderScanLibraryFiles'), id: 'traverse', icon: 'refresh' });
            }
            if ((options.items[0].Type === 'Movie' || options.items[0].Type === 'Episode') &&
                 options.items[0].CanDelete && options.mediaSourceId && options.items[0].MediaSources.length > 1) {
                result.push({
                    name: cjk
                        ? this.globalize.translate('Delete') + this.globalize.translate('Version')
                        : this.globalize.translate('Delete') + ' ' + this.globalize.translate('Version'),
                    id: 'delver_' + options.mediaSourceId,
                    icon: 'remove'
                });
            }
            if (options.items[0].hasOwnProperty('LockData') && options.items[0].Type !== 'CollectionFolder' &&
                (options.user && options.user.Policy.IsAdministrator || false)) {
                if (options.items[0].LockData) {
                    result.push({ name: unlockCommandName, id: 'unlock', icon: 'lock_open' });
                } else {
                    result.push({ name: lockCommandName, id: 'lock', icon: 'lock' });
                }
            }
            if ((options.items[0].Type === 'Series' || options.items[0].Type === 'Season') &&
                (options.user && options.user.Policy.IsAdministrator || false)) {
                const commandName = locale === 'zh-cn' ? '\u6E05\u9664\u7247\u5934\u6807\u8BB0' : 
                    (['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664\u7247\u982D\u6A19\u8A18' : 'Clear Intro Markers');
                result.push({ name: commandName, id: 'clear_intro', icon: 'clear_all' });
            }
            return result;
        }
        if (!options.multiSelect && options.items?.length > 1 && options.items[0].Type !== 'CollectionFolder' &&
            ((options.users && Object.values(options.users)[0]?.Policy.IsAdministrator) || false)) {
            const result = [];
            result.push({ name: lockCommandName, id: 'lock', icon: 'lock' });
            result.push({ name: unlockCommandName, id: 'unlock', icon: 'lock_open' });
            return result;
        }
        return [];
    },
    executeCommand: function(command, items) {
        if (!command || !items?.length) return;
        const actions = {
            copy: 'copy',
            remove: 'remove',
            traverse: 'traverse',
            lock: 'lock',
            unlock: 'unlock',
            clear_intro: 'clear_intro'
        };
        if (command.startsWith('delver_')) {
            const mediaSourceId = command.replace('delver_', '');
            const mediaSources = items[0].MediaSources || [];
            const matchingItem = mediaSources.find(source => source.Id === mediaSourceId);
            const itemId = matchingItem?.ItemId;
            const itemName = matchingItem?.Name;
            if (itemId && itemName) {
                return require(['components/strmassistant/strmassistant']).then(responses => {
                    return responses[0].delver(itemId, itemName, items[0].Type);
                });
            }
        }
        if (command === actions.lock || command === actions.unlock) {
            const lockData = command === actions.lock;
            return require(['components/strmassistant/strmassistant']).then(responses => {
                const promises = items.map(item => responses[0].lock(item.Id, lockData));
                return Promise.all(promises);
            });
        }
        if (actions[command]) {
            return require(['components/strmassistant/strmassistant']).then(responses => {
                if (command === 'traverse') {
                    return responses[0][actions[command]](items[0].ParentId);
                }
                return responses[0][actions[command]](items[0].Id, items[0].Name);
            });
        }
    }
};

setTimeout(() => {
    Emby.importModule('./modules/common/globalize.js').then(globalize => {
        strmAssistantCommandSource.globalize = globalize;
        Emby.importModule('./modules/common/itemmanager/itemmanager.js').then(itemmanager => {
            itemmanager.registerCommandSource(strmAssistantCommandSource);
        });
    });
}, 3000);
    ";
            var dataExplorer2Assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Emby.DataExplorer2");

            ModifiedShortcutsString = File.ReadAllText(Path.Combine(dashboardSourcePath, "modules", "shortcuts.js")) +
                                      injectShortcutCommand;

            if (dataExplorer2Assembly != null)
            {
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Info($"{nameof(ShortcutMenuHelper)} - Emby.DataExplorer2 plugin is installed");
                }

                var contextMenuHelperType = dataExplorer2Assembly.GetType("Emby.DataExplorer2.Api.ContextMenuHelper");
                var modifiedShortcutsProperty = contextMenuHelperType?.GetProperty("ModifiedShortcutsString",
                    BindingFlags.Static | BindingFlags.Public);
                var setMethod = modifiedShortcutsProperty?.GetSetMethod(true);

                if (setMethod != null)
                {
                    const string injectDataExplorerCommand = @"
const dataExplorerCommandSource = {
    getCommands(options) {
        const commands = [];
        if (options.items?.length === 1 && options.items[0].ProviderIds) {
            commands.push({
                name: 'Explore Item Data',
                id: 'dataexplorer',
                icon: 'manage_search'
            });
        }
        return commands;
    },
    executeCommand(command, items) {
        return require(['components/dataexplorer/dataexplorer']).then((responses) => {
            return responses[0].show(items[0].Id);
        });
    }
};

setTimeout(() => {
    Emby.importModule('./modules/common/itemmanager/itemmanager.js').then((itemmanager) => {
        itemmanager.registerCommandSource(dataExplorerCommandSource);
    });
}, 5000);
";
                    ModifiedShortcutsString += injectDataExplorerCommand;
                    setMethod.Invoke(null, new object[] { ModifiedShortcutsString });
                }
            }
        }
    }
}
