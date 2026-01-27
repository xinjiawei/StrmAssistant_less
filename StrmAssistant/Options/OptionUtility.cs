using Emby.Media.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using static StrmAssistant.Options.GeneralOptions;
using static StrmAssistant.Options.ModOptions;

namespace StrmAssistant.Options
{
    public static class Utility
    {
        private static readonly HashSet<string> _selectedExclusiveFeatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<long, ConcurrentDictionary<string, byte>> ItemExclusiveFeatures =
            new ConcurrentDictionary<long, ConcurrentDictionary<string, byte>>();

        private static HashSet<string> _selectedCatchupTasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _selectedIntroSkipPreferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static string[] _includeItemTypes = Array.Empty<string>();

        public static readonly HashSet<string> ExcludedCollectionTypes = new HashSet<string>
        {
            CollectionType.Books.ToString(),
            CollectionType.Photos.ToString(),
            CollectionType.Games.ToString(),
            CollectionType.LiveTv.ToString(),
            CollectionType.Playlists.ToString(),
            CollectionType.BoxSets.ToString()
        };

        public static readonly ExtraType[] IncludeExtraTypes =
        {
            ExtraType.AdditionalPart, ExtraType.BehindTheScenes, ExtraType.Clip, ExtraType.DeletedScene,
            ExtraType.Interview, ExtraType.Sample, ExtraType.Scene, ExtraType.ThemeSong, ExtraType.ThemeVideo,
            ExtraType.Trailer
        };

        public static readonly Version AppVer = Plugin.Instance.ApplicationHost.ApplicationVersion;
        public static readonly Version VerTarget = new Version("4.9.3.0");

        public static void InitializeOptionCache()
        {
            UpdateSearchScope(Plugin.Instance.MainOptionsStore.GetOptions().ModOptions.SearchScope);
        }


        public static void UpdateSearchScope(string currentScope)
        {
            var searchScope = currentScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries) ??
                              Array.Empty<string>();

            var includeItemTypes = new List<string>();

            foreach (var scope in searchScope)
            {
                if (Enum.TryParse(scope, true, out SearchItemType type))
                {
                    switch (type)
                    {
                        //case SearchItemType.Book:
                        //    includeItemTypes.AddRange(new[] { nameof(Book) });
                        //    break;
                        case SearchItemType.Collection:
                            includeItemTypes.AddRange(new[] { nameof(BoxSet) });
                            break;
                        case SearchItemType.Episode:
                            includeItemTypes.AddRange(new[] { nameof(Episode) });
                            break;
                        //case SearchItemType.Game:
                        //    includeItemTypes.AddRange(new[] { nameof(Game), nameof(GameSystem) });
                        //    break;
                        //case SearchItemType.Genre:
                        //    includeItemTypes.AddRange(new[] { nameof(MusicGenre), nameof(GameGenre), nameof(Genre) });
                        //    break;
                        case SearchItemType.LiveTv:
                            includeItemTypes.AddRange(new[]
                            {
                                nameof(LiveTvChannel), nameof(LiveTvProgram), "LiveTVSeries"
                            });
                            break;
                        case SearchItemType.Movie:
                            includeItemTypes.AddRange(new[] { nameof(Movie) });
                            break;
                        //case SearchItemType.Music:
                        //    includeItemTypes.AddRange(new[] { nameof(Audio), nameof(MusicVideo) });
                        //    break;
                        //case SearchItemType.MusicAlbum:
                        //    includeItemTypes.AddRange(new[] { nameof(MusicAlbum) });
                        //    break;
                        case SearchItemType.Person:
                            includeItemTypes.AddRange(new[] { nameof(Person) });
                            break;
                        //case SearchItemType.MusicArtist:
                        //   includeItemTypes.AddRange(new[] { nameof(MusicArtist) });
                        //    break;
                        //case SearchItemType.Photo:
                        //    includeItemTypes.AddRange(new[] { nameof(Photo) });
                        //    break;
                        //case SearchItemType.PhotoAlbum:
                        //    includeItemTypes.AddRange(new[] { nameof(PhotoAlbum) });
                        //    break;
                        case SearchItemType.Playlist:
                            includeItemTypes.AddRange(new[] { nameof(Playlist) });
                            break;
                        case SearchItemType.Series:
                            includeItemTypes.AddRange(new[] { nameof(Series) });
                            break;
                        case SearchItemType.Season:
                            includeItemTypes.AddRange(new[] { nameof(Season) });
                            break;
                        //case SearchItemType.Studio:
                        //    includeItemTypes.AddRange(new[] { nameof(Studio) });
                        //    break;
                        //case SearchItemType.Tag:
                        //    includeItemTypes.AddRange(new[] { nameof(Tag) });
                        //    break;
                        //case SearchItemType.Trailer:
                        //    includeItemTypes.AddRange(new[] { nameof(Trailer) });
                        //    break;
                        case SearchItemType.Video:
                            includeItemTypes.AddRange(new[] { nameof(Video) });
                            break;
                    }
                }
            }

            _includeItemTypes = includeItemTypes.ToArray();
        }

        public static string[] GetSearchScope()
        {
            return _includeItemTypes;
        }

        public static string[] GetValidLibraryIds(string scope)
        {
            var libraryIds = scope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            var validLibraryIds = Array.Empty<string>();

            if (libraryIds?.Any() is true)
            {
                var parsedIds = libraryIds.Select(id => long.TryParse(id, out var result) ? result : (long?)null)
                    .Where(id => id.HasValue)
                    .Select(id => id.Value)
                    .ToArray();

                if (parsedIds.Any())
                {
                    validLibraryIds = BaseItem.LibraryManager
                        .GetInternalItemIds(new InternalItemsQuery { ItemIds = parsedIds })
                        .Select(id => id.ToString())
                        .ToArray();
                }
            }

            return validLibraryIds;
        }
    }
}
