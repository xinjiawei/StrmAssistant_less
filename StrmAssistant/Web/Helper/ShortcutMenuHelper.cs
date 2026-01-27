using HarmonyLib;
using StrmAssistant.Mod;
using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace StrmAssistant.Web.Helper
{
    internal static class ShortcutMenuHelper
    {
        private static readonly PatchTracker PatchTracker =
            new PatchTracker(typeof(ShortcutMenuHelper), PatchApproach.Injection, "ShortcutMenuHelper");

        public static ReadOnlyMemory<byte> ModifiedShortcutsBytes { get; private set; }
        public static ReadOnlyMemory<byte> StrmAssistantJsBytes { get; private set; }

        public static void Initialize()
        {
            try
            {
                StrmAssistantJsBytes = GetResourceBytes("strmassistant.js");
                var configurationManager = Plugin.Instance.ConfigurationManager;
                var dashboardSourcePath = configurationManager.Configuration.DashboardSourcePath ??
                                          Path.Combine(configurationManager.ApplicationPaths.ApplicationResourcesPath,
                                              "dashboard-ui");
                ModifyShortcutMenu(dashboardSourcePath);
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Error($"{PatchTracker.Name} Init Failed");
                Plugin.Instance.Logger.Error(e.Message);
                Plugin.Instance.Logger.Debug(e.StackTrace);
            }
        }

        private static ReadOnlyMemory<byte> GetResourceBytes(string resourceName)
        {
            var name = typeof(Plugin).Namespace + ".Web.Resources." + resourceName;
            using var stream = typeof(ShortcutMenuHelper).GetTypeInfo().Assembly.GetManifestResourceStream(name) ??
                               throw new InvalidOperationException($"Resource not found: {name}");

            var length = (int)stream.Length;
            var buffer = new byte[length];

            var bytesRead = stream.Read(buffer, 0, length);
            if (bytesRead != length)
            {
                throw new EndOfStreamException($"Could not read entire resource: {name}");
            }

            return new ReadOnlyMemory<byte>(buffer);
        }

        private static void ModifyShortcutMenu(string dashboardSourcePath)
        {
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
        const clearIntroCommandName = locale === 'zh-cn' ? '\u6E05\u9664\u7247\u5934\u6807\u8BB0' : 
            (['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664\u7247\u982D\u6A19\u8A18' : 'Clear Intro Markers');

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
                result.push({ name: lockCommandName, id: 'lock', icon: 'lock' });
                result.push({ name: unlockCommandName, id: 'unlock', icon: 'lock_open' });
            }
            if ((options.items[0].Type === 'Series' || options.items[0].Type === 'Season') &&
                (options.user && options.user.Policy.IsAdministrator || false)) {
                result.push({ name: clearIntroCommandName, id: 'clear_intro', icon: 'clear_all' });
            }
            return result;
        }
        if (!options.multiSelect && options.items?.length > 1 && options.items[0].Type !== 'CollectionFolder' &&
            ((options.users && Object.values(options.users)[0]?.Policy.IsAdministrator) || false)) {
            const result = [];
            result.push({ name: lockCommandName, id: 'lock', icon: 'lock' });
            result.push({ name: unlockCommandName, id: 'unlock', icon: 'lock_open' });
            if (options.items[0].Type === 'Series' || options.items[0].Type === 'Season') {
                result.push({ name: clearIntroCommandName, id: 'clear_intro', icon: 'clear_all' });
            }
            return result;
        }
        return [];
    },
    executeCommand: function(command, items, options) {
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
        if (command === actions.clear_intro) {
            return require(['components/strmassistant/strmassistant']).then(responses => {
                const locale = this.globalize.getCurrentLocale().toLowerCase();
                const commandName = locale === 'zh-cn' ? '\u6E05\u9664\u7247\u5934\u6807\u8BB0' : 
                        (['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664\u7247\u982D\u6A19\u8A18' : 'Clear Intro Markers');
                this.confirm({
                    text: this.globalize.translate('AreYouSureToContinue'),
                    title: commandName,
                    confirmText: this.globalize.translate('Clear'),
                    primary: 'cancel'
                }).then(() => {
                    const promises = items.map(item => responses[0].clear_intro(item.Id));
                    return Promise.all(promises);
                }).then(() => {
                    const confirmMessage = (locale === 'zh-cn') ? commandName + '\u6210\u529F' : 
                        (['zh-hk', 'zh-tw'].includes(locale) ? commandName + '\u6210\u529F' : commandName + ' Success');
                    this.toast(confirmMessage);
                });
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
    Promise.all([
        Emby.importModule('./modules/common/globalize.js'),
        Emby.importModule('./modules/common/dialogs/confirm.js'),
        Emby.importModule('./modules/toast/toast.js'),
        Emby.importModule('./modules/common/itemmanager/itemmanager.js')
    ]).then(([globalize, confirm, toast, itemmanager]) => {
        strmAssistantCommandSource.globalize = globalize;
        strmAssistantCommandSource.confirm = confirm;
        strmAssistantCommandSource.toast = toast;
        itemmanager.registerCommandSource(strmAssistantCommandSource);
    });
}, 3000);
    ";
            var modifiedShortcutsString =
                File.ReadAllText(Path.Combine(dashboardSourcePath, "modules", "shortcuts.js")) + injectShortcutCommand;
            ModifiedShortcutsBytes = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(modifiedShortcutsString));

            var contextMenuHelperType = AccessTools.TypeByName("Emby.DataExplorer2.Api.ContextMenuHelper");

            if (contextMenuHelperType is null) return;

            if (Plugin.Instance.DebugMode)
            {
                Plugin.Instance.Logger.Debug($"{nameof(ShortcutMenuHelper)} - Emby.DataExplorer2 plugin is installed");
            }

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
    executeCommand(command, items, options) {
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
            modifiedShortcutsString += injectDataExplorerCommand;
            Traverse.Create(contextMenuHelperType).Property("ModifiedShortcutsString")
                .SetValue(modifiedShortcutsString);
        }
    }
}
