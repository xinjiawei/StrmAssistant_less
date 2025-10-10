using HarmonyLib;
using MediaBrowser.Controller.Entities;
using SQLitePCL.pretty;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using static StrmAssistant.Mod.PatchManager;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.Mod
{
    public class EnhanceChineseSearch : PatchBase<EnhanceChineseSearch>
    {
        private static readonly Version AppVer = Plugin.Instance.ApplicationHost.ApplicationVersion;
        private static readonly Version Ver4830 = new Version("4.8.3.0");
        private static readonly Version Ver4900 = new Version("4.9.0.0");
        private static readonly Version Ver4937 = new Version("4.9.0.37");

        private static Type raw;
        private static MethodInfo sqlite3_enable_load_extension;
        private static FieldInfo sqlite3_db;
        private static MethodInfo _createConnection;
        private static PropertyInfo _dbFilePath;
        private static MethodInfo _getJoinCommandText;
        private static MethodInfo _createSearchTerm;
        private static MethodInfo _cacheIdsFromTextParams;

        public static string CurrentTokenizerName { get; private set; } = "unknown";

        private static string _tokenizerPath;
        private static readonly object _lock = new object();
        private static bool _patchPhase2Initialized;
        private static readonly Dictionary<string, Regex> patterns = new Dictionary<string, Regex>
        {
            { "imdb", new Regex(@"^tt\d{7,8}$", RegexOptions.IgnoreCase | RegexOptions.Compiled) },
            { "tmdb", new Regex(@"^tmdb(id)?=(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled) },
            { "tvdb", new Regex(@"^tvdb(id)?=(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled) }
        };

        public EnhanceChineseSearch()
        {

            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT when Environment.Is64BitOperatingSystem:
                    _tokenizerPath = Path.Combine(Plugin.Instance.ApplicationPaths.PluginsPath, "simple.dll");
                    break;
                case PlatformID.Unix when Environment.Is64BitOperatingSystem:
                    _tokenizerPath = Path.Combine(Plugin.Instance.ApplicationPaths.PluginsPath, "libsimple");
                    break;
                default:
                    ResetOptions();
                    break;
            }

            Initialize();

            if (Plugin.Instance.MainOptionsStore.GetOptions().ModOptions.EnhanceChineseSearch ||
                Plugin.Instance.MainOptionsStore.GetOptions().ModOptions.EnhanceChineseSearchRestore)
            {
                if (AppVer >= Ver4830)
                {
                    PatchPhase1();
                }
                else
                {
                    ResetOptions();
                }
            }
        }

        protected override void OnInitialize()
        {
            var sqlitePCLEx = Assembly.Load("SQLitePCLRawEx.core");
            raw = sqlitePCLEx.GetType("SQLitePCLEx.raw");
            sqlite3_enable_load_extension = raw.GetMethod("sqlite3_enable_load_extension",
                BindingFlags.Static | BindingFlags.Public);

            sqlite3_db =
                typeof(SQLiteDatabaseConnection).GetField("db", BindingFlags.NonPublic | BindingFlags.Instance);

            var embySqlite = Assembly.Load("Emby.Sqlite");
            var baseSqliteRepository = embySqlite.GetType("Emby.Sqlite.BaseSqliteRepository");
            _createConnection = baseSqliteRepository.GetMethod("CreateConnection",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(bool), typeof(CancellationToken) },
                null);
            _dbFilePath =
                baseSqliteRepository.GetProperty("DbFilePath", BindingFlags.NonPublic | BindingFlags.Instance);

            var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
            var sqliteItemRepository =
                embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.Data.SqliteItemRepository");
            _getJoinCommandText = sqliteItemRepository.GetMethod(
                "GetJoinCommandText",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] {
                    typeof(InternalItemsQuery),
                    typeof(List<KeyValuePair<string, string>>),
                    typeof(string),
                    typeof(string), // itemLinks2TableQualifier 参数
                    typeof(bool)    // allowJoinOnItemLinks 参数
                },
                null
                );
            _createSearchTerm =
                sqliteItemRepository.GetMethod("CreateSearchTerm", BindingFlags.NonPublic | BindingFlags.Static);
            _cacheIdsFromTextParams = sqliteItemRepository.GetMethod("CacheIdsFromTextParams",
                BindingFlags.Instance | BindingFlags.NonPublic);
        }

        protected override void Prepare(bool apply)
        {
            // No action needed
        }

        private static void PatchPhase1()
        {
            if (EnsureTokenizerExists() && PatchUnpatch(Instance.PatchTracker, true, _createConnection,
                    postfix: nameof(CreateConnectionPostfix))) return;

            if (Plugin.Instance.DebugMode)
            {
                Plugin.Instance.Logger.Debug("EnhanceChineseSearch 1207 - PatchPhase1 Failed");
            }
            
            ResetOptions();
        }

        private static void PatchPhase2(IDatabaseConnection connection)
        {
            string ftsTableName;

            if (AppVer >= Ver4830)
            {
                ftsTableName = "fts_search9";
            }
            else
            {
                ftsTableName = "fts_search8";
            }

            var tokenizerCheckQuery = $@"
                SELECT 
                    CASE 
                        WHEN instr(sql, 'tokenize=""simple""') > 0 THEN 'simple'
                        WHEN instr(sql, 'tokenize=""unicode61 remove_diacritics 2""') > 0 THEN 'unicode61 remove_diacritics 2'
                        ELSE 'unknown'
                    END AS tokenizer_name
                FROM 
                    sqlite_master 
                WHERE 
                    type = 'table' AND 
                    name = '{ftsTableName}';";

            var rebuildFtsResult = true;
            var patchSearchFunctionsResult = false;

            try
            {
                using (var statement = connection.PrepareStatement(tokenizerCheckQuery))
                {
                    if (statement.MoveNext())
                    {
                        CurrentTokenizerName = statement.Current?.GetString(0) ?? "unknown";
                    }
                }

                Plugin.Instance.Logger.Info("EnhanceChineseSearch - Current tokenizer (before) is " + CurrentTokenizerName);

                if (!string.Equals(CurrentTokenizerName, "unknown", StringComparison.Ordinal))
                {
                    if (Plugin.Instance.MainOptionsStore.GetOptions().ModOptions.EnhanceChineseSearchRestore)
                    {
                        if (string.Equals(CurrentTokenizerName, "simple", StringComparison.Ordinal))
                        {
                            rebuildFtsResult = RebuildFts(connection, ftsTableName, "unicode61 remove_diacritics 2");
                        }
                        if (rebuildFtsResult)
                        {
                            CurrentTokenizerName = "unicode61 remove_diacritics 2";
                            Plugin.Instance.Logger.Info("EnhanceChineseSearch - Restore Success");
                        }
                        ResetOptions();
                    }
                    else if (Plugin.Instance.MainOptionsStore.GetOptions().ModOptions.EnhanceChineseSearch)
                    {
                        // PatchUnpatch
                        patchSearchFunctionsResult = PatchSearchFunctions();

                        if (patchSearchFunctionsResult)
                        {
                            if (string.Equals(CurrentTokenizerName, "unicode61 remove_diacritics 2", StringComparison.Ordinal))
                            {
                                rebuildFtsResult = RebuildFts(connection, ftsTableName, "simple");
                            }

                            if (rebuildFtsResult)
                            {
                                CurrentTokenizerName = "simple";
                                Plugin.Instance.Logger.Info("EnhanceChineseSearch - Load Success");
                            }
                        }
                    }
                }

                Plugin.Instance.Logger.Info("EnhanceChineseSearch - Current tokenizer (after) is " + CurrentTokenizerName);
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Info("EnhanceChineseSearch - PatchPhase2 Failed");
                    Plugin.Instance.Logger.Info(e.Message);
                    Plugin.Instance.Logger.Info(e.StackTrace);
                }
            }

            if (!patchSearchFunctionsResult || !rebuildFtsResult ||
                string.Equals(CurrentTokenizerName, "unknown", StringComparison.Ordinal))
            {
                ResetOptions();
            }
        }

        private static bool RebuildFts(IDatabaseConnection connection, string ftsTableName, string tokenizerName)
        {
            string populateQuery;

            if (AppVer < Ver4900)
            {
                populateQuery =
                    $"insert into {ftsTableName}(RowId, Name, OriginalTitle, SeriesName, Album) select id, " +
                    GetSearchColumnNormalization("Name") + ", " +
                    GetSearchColumnNormalization("OriginalTitle") + ", " +
                    GetSearchColumnNormalization("SeriesName") + ", " +
                    GetSearchColumnNormalization("Album") +
                    " from MediaItems";
            }
            else
            {
                populateQuery =
                    $"insert into {ftsTableName}(RowId, Name, OriginalTitle, SeriesName, Album) select id, " +
                    GetSearchColumnNormalization("Name") + ", " +
                    GetSearchColumnNormalization("OriginalTitle") + ", " +
                    GetSearchColumnNormalization("SeriesName") + ", " +
                    GetSearchColumnNormalization(
                        "(select case when AlbumId is null then null else (select name from MediaItems where Id = AlbumId limit 1) end)") +
                    " from MediaItems";
            }

            connection.BeginTransaction(TransactionMode.Deferred);
            try
            {
                var dropFtsTableQuery = $"DROP TABLE IF EXISTS {ftsTableName}";
                connection.Execute(dropFtsTableQuery);

                var createFtsTableQuery =
                    $"CREATE VIRTUAL TABLE IF NOT EXISTS {ftsTableName} USING FTS5 (Name, OriginalTitle, SeriesName, Album, tokenize=\"{tokenizerName}\", prefix='1 2 3 4')";
                connection.Execute(createFtsTableQuery);

                Plugin.Instance.Logger.Info($"EnhanceChineseSearch - Filling {ftsTableName} Start");

                connection.Execute(populateQuery);
                connection.CommitTransaction();

                Plugin.Instance.Logger.Info($"EnhanceChineseSearch - Filling {ftsTableName} Complete");

                return true;
            }
            catch (Exception e)
            {
                connection.RollbackTransaction();

                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Info("EnhanceChineseSearch - RebuildFts Failed");
                    Plugin.Instance.Logger.Info(e.Message);
                    Plugin.Instance.Logger.Info(e.StackTrace);
                }
            }

            return false;
        }

        private static string GetSearchColumnNormalization(string columnName)
        {
            return "replace(replace(" + columnName + ",'''',''),'.','')";
        }

        private static bool EnsureTokenizerExists()
        {
            var resourceName = GetTokenizerResourceName();
            var expectedSha1 = GetExpectedSha1();

            if (resourceName == null || expectedSha1 == null) return false;

            try
            {
                if (File.Exists(_tokenizerPath))
                {
                    var existingSha1 = ComputeSha1(_tokenizerPath);
                    Plugin.Instance.Logger.Debug(
                               $"EnhanceChineseSearch 1207 - Tokenizer SHA-1:{existingSha1}");

                    if (expectedSha1.ContainsValue(existingSha1))
                    {
                        var highestVersion = expectedSha1.Keys.Max();
                        var highestSha1 = expectedSha1[highestVersion];

                        if (existingSha1 == highestSha1)
                        {
                            Plugin.Instance.Logger.Debug(
                                $"EnhanceChineseSearch 1208 - Tokenizer exists with matching SHA-1 for the highest version {highestVersion}");
                        }
                        else
                        {
                            var currentVersion = expectedSha1.FirstOrDefault(x => x.Value == existingSha1).Key;
                            Plugin.Instance.Logger.Debug(
                                $"EnhanceChineseSearch 1209 - Tokenizer exists for version {currentVersion} but does not match the highest version {highestVersion}. Upgrading...");
                            ExportTokenizer(resourceName);
                        }

                        return true;
                    }

                    Plugin.Instance.Logger.Debug(
                        "EnhanceChineseSearch 1210 - Tokenizer exists but SHA-1 is not recognized. No action taken.");

                    return true;
                }

                Plugin.Instance.Logger.Debug("EnhanceChineseSearch 1211 - Tokenizer does not exist. Exporting...");
                ExportTokenizer(resourceName);

                return true;
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug("EnhanceChineseSearch 1213 - EnsureTokenizerExists Failed");
                    Plugin.Instance.Logger.Debug(e.Message);
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                }
            }

            return false;
        }

        private static void ExportTokenizer(string resourceName)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                using (var fileStream = new FileStream(_tokenizerPath, FileMode.Create, FileAccess.Write))
                {
                    stream.CopyTo(fileStream);
                }
            }

            Plugin.Instance.Logger.Debug($"EnhanceChineseSearch 1212 - Exported {resourceName} to {_tokenizerPath}");
        }

        private static string GetTokenizerResourceName()
        {
            var tokenizerNamespace = Assembly.GetExecutingAssembly().GetName().Name + ".Tokenizer";
            var winSimpleTokenizer = $"{tokenizerNamespace}.win.simple.dll";
            var linuxSimpleTokenizer = $"{tokenizerNamespace}.linux.libsimple.so";

            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT when Environment.Is64BitOperatingSystem:
                    return winSimpleTokenizer;
                case PlatformID.Unix when Environment.Is64BitOperatingSystem:
                    return linuxSimpleTokenizer;
                default:
                    return null;
            }
        }

        private static Dictionary<Version, string> GetExpectedSha1()
        {
            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT:
                    return new Dictionary<Version, string>
                    {
                        { new Version(0, 4, 0), "a83d90af9fb88e75a1ddf2436c8b67954c761c83" },
                        { new Version(0, 5, 0), "aed57350b46b51bb7d04321b7fe8e5e60b0cdbdc" },
                        { new Version(0, 5, 2), "338bb0915d6f4625b54f041bdeb6791b6e590c4e" },
                        { new Version(0, 5, 3), "338bb0915d6f4625b54f041bdeb6791b6e590c4e" }
                        // 
                    };
                case PlatformID.Unix:
                    return new Dictionary<Version, string>
                    {
                        { new Version(0, 4, 0), "f7fb8ba0b98e358dfaa87570dc3426ee7f00e1b6" },
                        { new Version(0, 5, 0), "8e36162f96c67d77c44b36093f31ae4d297b15c0" },
                        { new Version(0, 5, 2), "e89eeb7938894e4e8b284896285e7dc90da715bc" },
                        { new Version(0, 5, 3), "a6188af48c0fef201cb24dbebc65c4cf5b4ddf9b" }
                    };
                default:
                    return null;
            }
        }

        private static string ComputeSha1(string filePath)
        {
            using (var sha1 = SHA1.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hash = sha1.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static void ResetOptions()
        {
            Plugin.Instance.MainOptionsStore.GetOptions().ModOptions.EnhanceChineseSearch = false;
            Plugin.Instance.MainOptionsStore.SavePluginOptionsSuppress();
        }

        private static bool PatchSearchFunctions()
        {

            Plugin.Instance.Logger.Debug("EnhanceChineseSearch - PatchUnpatch _getJoinCommandText");

            bool patchedJoinCommand = PatchUnpatch(
                Instance.PatchTracker,
                true,
                _getJoinCommandText,
                postfix: nameof(GetJoinCommandTextPostfix)
            );

            Plugin.Instance.Logger.Debug("EnhanceChineseSearch - PatchUnpatch _createSearchTerm");
            bool patchedSearchTerm = PatchUnpatch(
                Instance.PatchTracker,
                true,
                _createSearchTerm,
                prefix: nameof(CreateSearchTermPrefix)
            );

            Plugin.Instance.Logger.Debug("EnhanceChineseSearch - PatchUnpatch _cacheIdsFromTextParams");
            bool patchedCacheIds = PatchUnpatch(
                Instance.PatchTracker,
                true,
                _cacheIdsFromTextParams,
                prefix: nameof(CacheIdsFromTextParamsPrefix)
            );

            return patchedJoinCommand && patchedSearchTerm && patchedCacheIds;
        }

        private static bool LoadTokenizerExtension(IDatabaseConnection connection)
        {
            try
            {

                var db = sqlite3_db.GetValue(connection);
                sqlite3_enable_load_extension.Invoke(raw, new[] { db, 1 });
                Plugin.Instance.Logger.Debug("LoadTokenizerExtension 1301 - _tokenizerPath: " +  _tokenizerPath);
                connection.Execute("SELECT load_extension('" + _tokenizerPath + "')");

                return true;
            }
            catch (SQLitePCL.pretty.SQLiteException ex)
            {
                Plugin.Instance.Logger.Error("Failed to load extension: " + ex.Message);
                // 可以打印更详细的错误信息
                Plugin.Instance.Logger.Error(ex.StackTrace);
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Warn("EnhanceChineseSearch - Load tokenizer failed.");
                    Plugin.Instance.Logger.Info(e.Message);
                    Plugin.Instance.Logger.Info(e.StackTrace);
                }
            }

            return false;
        }

        [HarmonyPostfix]
        private static void CreateConnectionPostfix(object __instance, [HarmonyArgument("isReadOnly")] bool isReadOnly, [HarmonyArgument("cancellationToken")] CancellationToken cancellationToken,
            ref IDatabaseConnection __result)
        {
            if (!isReadOnly)
            {
                Plugin.Instance.Logger.Debug("EnhanceChineseSearch - CreateConnectionPostfix: " + isReadOnly + " " + _patchPhase2Initialized);
            }
           
            if (!isReadOnly && !_patchPhase2Initialized)
            {
                lock (_lock)
                {
                    Plugin.Instance.Logger.Debug("EnhanceChineseSearch - CreateConnectionPostfix Start: " + isReadOnly + " " + _patchPhase2Initialized);
                    if (!_patchPhase2Initialized)
                    {
                        var db = _dbFilePath.GetValue(__instance) as string;
                        if (db?.EndsWith("library.db", StringComparison.OrdinalIgnoreCase) != true)
                        {
                            Plugin.Instance.Logger.Debug("EnhanceChineseSearch - _dbFilePath Err");
                            return;
                        }

                        var tokenizerLoaded = LoadTokenizerExtension(__result);

                        if (tokenizerLoaded)
                        {
                            Plugin.Instance.Logger.Info("EnhanceChineseSearch - tokenizerLoaded Load Success");
                            _patchPhase2Initialized = true;
                            PatchPhase2(__result);
                        }
                        else
                        {
                            Plugin.Instance.Logger.Info("EnhanceChineseSearch - tokenizerLoaded Load Err"); 
                        }
                    }
                }
            }
        }

        [HarmonyPostfix]
        private static void GetJoinCommandTextPostfix(
            InternalItemsQuery query,
            List<KeyValuePair<string, string>> bindParams, 
            string mediaItemsTableQualifier, 
            ref StringBuilder __result)
        {
            var sql = __result.ToString();
            var newSql = sql;

            bool hasMatchParam =
                newSql.IndexOf("match @SearchTerm", StringComparison.OrdinalIgnoreCase) >= 0
                || System.Text.RegularExpressions.Regex.IsMatch(newSql, @"\bmatch\b\s*\(?\s*@SearchTerm\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (!string.IsNullOrEmpty(query.SearchTerm) && hasMatchParam)
            {
                var replacement = Plugin.Instance.MainOptionsStore.GetOptions().ModOptions.ExcludeOriginalTitleFromSearch
                    ? "match '-OriginalTitle:' || simple_query(@SearchTerm)"
                    : "match simple_query(@SearchTerm)";

                newSql = System.Text.RegularExpressions.Regex.Replace(
                    newSql,
                    @"\bmatch\b\s*\(?\s*@SearchTerm\b",
                    replacement,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );
            }

            if (!string.IsNullOrEmpty(query.Name) && hasMatchParam)
            {
                newSql = System.Text.RegularExpressions.Regex.Replace(
                    newSql,
                    @"\bmatch\b\s*\(?\s*@SearchTerm\b",
                    "match 'Name:' || simple_query(@SearchTerm)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );

                for (var i = 0; i < bindParams.Count; i++)
                {
                    var kvp = bindParams[i];
                    if (kvp.Key == "@SearchTerm")
                    {
                        var currentValue = kvp.Value;

                        if (currentValue.StartsWith("Name:", StringComparison.Ordinal))
                        {
                            currentValue = currentValue
                                .Substring(currentValue.IndexOf(":", StringComparison.Ordinal) + 1)
                                .Trim('\"', '^', '$')
                                .Replace(".", string.Empty)
                                .Replace("'", string.Empty);
                        }

                        bindParams[i] = new KeyValuePair<string, string>(kvp.Key, currentValue);
                    }
                }
            }

            if (!ReferenceEquals(sql, newSql) && !string.Equals(sql, newSql, StringComparison.Ordinal))
            {
                __result.Clear().Append(newSql);
            }
        }

        [HarmonyPrefix]
        private static bool CreateSearchTermPrefix(string searchTerm, ref string __result)
        {
            __result = searchTerm.Replace(".", string.Empty).Replace("'", string.Empty);

            return false;
        }

        [HarmonyPrefix]
        private static bool CacheIdsFromTextParamsPrefix(InternalItemsQuery query, IDatabaseConnection db)
        {
            if ((query.PersonTypes?.Length ?? 0) == 0)
            {
                var nameStartsWith = query.NameStartsWith;
                if (!string.IsNullOrEmpty(nameStartsWith))
                {
                    query.SearchTerm = nameStartsWith;
                    query.NameStartsWith = null;
                }

                var searchTerm = query.SearchTerm;
                if (query.IncludeItemTypes.Length == 0 && !string.IsNullOrEmpty(searchTerm))
                {
                    query.IncludeItemTypes = GetSearchScope();
                }

                if (!string.IsNullOrEmpty(searchTerm))
                {
                    foreach (var provider in patterns)
                    {
                        var match = provider.Value.Match(searchTerm.Trim());
                        if (match.Success)
                        {
                            var idValue = provider.Key == "imdb" ? match.Value : match.Groups[2].Value;

                            query.AnyProviderIdEquals = new List<KeyValuePair<string, string>>
                            {
                                new KeyValuePair<string, string>(provider.Key, idValue)
                            };
                            query.SearchTerm = null;
                            break;
                        }
                    }
                }

                if (AppVer >= Ver4937 && !string.IsNullOrEmpty(query.SearchTerm))
                {
                    var result = LoadTokenizerExtension(db);
                }
            }

            return true;
        }
    }
}
