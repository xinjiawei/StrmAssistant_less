using Emby.Media.Common.Extensions;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.Options
{
    public class ModOptions : EditableOptionsBase
    {
        [DisplayNameL("PluginOptions_ModOptions_Mod_Features", typeof(Resources))]
        public override string EditorTitle => Resources.PluginOptions_ModOptions_Mod_Features;

        [DisplayNameL("ModOptions_EnhanceChineseSearch_Enhance_Chinese_Search", typeof(Resources))]
        [DescriptionL("ModOptions_EnhanceChineseSearch_Support_Chinese_fuzzy_search_and_Pinyin_search__Default_is_OFF_", typeof(Resources))]
        [Required]
        [EnabledCondition(nameof(IsChineseSearchSupported), SimpleCondition.IsTrue)]
        public bool EnhanceChineseSearch { get; set; } = false;

        [Browsable(false)]
        public bool EnhanceChineseSearchRestore { get; set; } = false;

        public enum SearchItemType
        {
            [DescriptionL("ItemType_Movie_Movie", typeof(Resources))] Movie,
            [DescriptionL("ItemType_Collection_Collection", typeof(Resources))] Collection,
            [DescriptionL("ItemType_Series_Series", typeof(Resources))] Series,
            [DescriptionL("ItemType_Season_Season", typeof(Resources))] Season,
            [DescriptionL("ItemType_Episode_Episode", typeof(Resources))] Episode,
            [DescriptionL("ItemType_Person_Person", typeof(Resources))] Person,
            [DescriptionL("ItemType_LiveTv_LiveTv", typeof(Resources))] LiveTv,

            [DescriptionL("ItemType_Playlist_Playlist", typeof(Resources))] Playlist,
            [DescriptionL("ItemType_Video_Video", typeof(Resources))] Video,
        }

        [Browsable(false)]
        public List<EditorSelectOption> SearchItemTypeList { get; set; } = new List<EditorSelectOption>();

        [DisplayNameL("ModOptions_SearchScope_Search_Scope", typeof(Resources))]
        [DescriptionL("ModOptions_SearchScope_Include_item_types__Blank_includes_all_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(SearchItemTypeList))]
        [VisibleCondition(nameof(EnhanceChineseSearch), SimpleCondition.IsTrue)]
        public string SearchScope { get; set; } =
            string.Join(",", new[] { SearchItemType.Movie, SearchItemType.Collection, SearchItemType.Series });

        [DisplayNameL("ModOptions_ExcludeOriginalTitle_Exclude_Original_Title", typeof(Resources))]
        [DescriptionL("ModOptions_ExcludeOriginalTitle_Exclude_original_title_from_search__Default_is_OFF_", typeof(Resources))]
        [Required]
        [VisibleCondition(nameof(EnhanceChineseSearch), SimpleCondition.IsTrue)]
        public bool ExcludeOriginalTitleFromSearch { get; set; } = false;

        [Browsable(false)]
        public bool IsChineseSearchSupported =>
            EnhanceChineseSearch || Plugin.IsModSupported &&
            (AppVer == VerTarget);

        public void Initialize()
        {
            SearchItemTypeList.Clear();

            foreach (Enum item in Enum.GetValues(typeof(SearchItemType)))
            {
                var selectOption = new EditorSelectOption
                {
                    Value = item.ToString(),
                    Name = EnumExtensions.GetDescription(item),
                    IsEnabled = true,
                };

                SearchItemTypeList.Add(selectOption);
            }
        }
    }
}
