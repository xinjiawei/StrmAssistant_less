using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.ComponentModel;
using System.Reflection;

namespace StrmAssistant.Options
{
    public class AboutOptions : EditableOptionsBase
    {
        [DisplayNameL("AboutOptions_EditorTitle_About", typeof(Resources))]
        public override string EditorTitle => Resources.AboutOptions_EditorTitle_About;

        public GenericItemList VersionInfoList { get; set; } = new GenericItemList();

        [Browsable(false)]
        public string DefaultUICulture { get; set; } = "zh-CN";

        [Browsable(false)]
        public bool DebugMode { get; set; } = true;

        [Browsable(false)]
        public string GitHubToken { get; set; } = string.Empty;

        [Browsable(false)]
        public string GitHubProxy { get; set; } = string.Empty;
        
        private static string GetVersionHash()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            var fullVersion = assembly.GetName().Version?.ToString();

            if (informationalVersion != null)
            {
                var parts = informationalVersion.Split('+');
                var shortCommitHash = parts.Length > 1 ? parts[1].Substring(0, 7) : "n/a";
                return $"{fullVersion}+{shortCommitHash}";
            }

            return fullVersion;
        }

        public void Initialize()
        {
            VersionInfoList.Clear();

            VersionInfoList.Add(
            new GenericListItem
            {
                PrimaryText = Resources.About_Fork,
                Icon = IconNames.title,
                IconMode = ItemListIconMode.SmallRegular,
                HyperLink = "https://blog.jiawei.xin/?p=1960",
            });

            VersionInfoList.Add(
                new GenericListItem
                {
                    PrimaryText = GetVersionHash(),
                    Icon = IconNames.info,
                    IconMode = ItemListIconMode.SmallRegular
                });
            VersionInfoList.Add(
                new GenericListItem
                {
                    PrimaryText = Resources.Blog_Url,
                    Icon = IconNames.code,
                    IconMode = ItemListIconMode.SmallRegular,
                    HyperLink = "https://blog.jiawei.xin/?p=1960",
                });

            VersionInfoList.Add(
                new GenericListItem
                {
                    PrimaryText = Resources.Repo_Link,
                    Icon = IconNames.code,
                    IconMode = ItemListIconMode.SmallRegular,
                    HyperLink = "https://github.com/xinjiawei/StrmAssistant_less",
                });
            VersionInfoList.Add(
                new GenericListItem
                {
                    PrimaryText = Resources.Original_Repo_Link,
                    Icon = IconNames.code,
                    IconMode = ItemListIconMode.SmallRegular,
                    HyperLink = "https://github.com/sjtuross/StrmAssistant",
                });
            VersionInfoList.Add(
                new GenericListItem
                {
                    PrimaryText = Resources.About_StrmAssistant_Pro,
                    Icon = IconNames.code,
                    IconMode = ItemListIconMode.SmallRegular,
                    HyperLink = "https://github.com/sjtuross/StrmAssistant.Releases/releases/tag/v3.0.0.0\r\n",
                });
        }
    }
}
