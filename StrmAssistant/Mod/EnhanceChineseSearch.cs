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
using System.Runtime.InteropServices;
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
        private static readonly string StockTokenizerName = "unicode61 remove_diacritics 2";
        private static readonly string SimpleTokenizerName = "simple";
        private static readonly string FtsTableName = AppVer >= Ver4830 ? "fts_search9" : "fts_search8";

        public static string CurrentTokenizerName { get; private set; } = "unknown";

        private static readonly string TokenizerPath =
            Path.Combine(Plugin.Instance.ApplicationPaths.PluginsPath, "libsimple.so");
        private static readonly object _lock = new object();
        private static bool _patchPhase2Initialized;
        private static bool _searchPatchCompleted;

        private static readonly Dictionary<string, Regex> ProviderPatterns = new Dictionary<string, Regex>
        {
            { "imdb", new Regex(@"^tt\d{7,8}$", RegexOptions.IgnoreCase | RegexOptions.Compiled) },
            { "tmdb", new Regex(@"^tmdb(id)?=(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled) },
            { "tvdb", new Regex(@"^tvdb(id)?=(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled) }
        };

        private static Type _sqlitePCLRawExRaw;
        private static MethodInfo _sqlite3_enable_load_extension;
        private static FieldInfo _sqlite3_db;
        private static MethodInfo _createConnection;
        private static PropertyInfo _dbFilePath;
        private static MethodInfo _getJoinCommandText;
        private static MethodInfo _createSearchTerm;
        private static MethodInfo _cacheIdsFromTextParams;

        public EnhanceChineseSearch()
        {
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
            try
            {
                var sqlitePCLEx = Assembly.Load("SQLitePCLRawEx.core");
                _sqlitePCLRawExRaw = sqlitePCLEx.GetType("SQLitePCLEx.raw");
                _sqlite3_enable_load_extension = _sqlitePCLRawExRaw.GetMethod("sqlite3_enable_load_extension",
                    BindingFlags.Static | BindingFlags.Public);

                if (_sqlite3_enable_load_extension != null)
                {
                    var parameters = _sqlite3_enable_load_extension.GetParameters();
                    if (parameters.Length != 2 ||
                        parameters[0].ParameterType.FullName != "SQLitePCLEx.sqlite3" ||
                        parameters[1].ParameterType != typeof(int))
                    {
                        _sqlite3_enable_load_extension = null;
                    }
                }
            }
            catch { }

            try
            {
                _sqlite3_db = typeof(SQLiteDatabaseConnection).GetField("db", BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch { }

            try
            {
                Assembly sqliteAssembly = null;
                var candidateAssemblies = new[] { "Emby.Sqlite", "MediaBrowser.Sqlite" };
                foreach (var assemblyName in candidateAssemblies)
                {
                    try
                    {
                        sqliteAssembly = Assembly.Load(assemblyName);
                        break;
                    }
                    catch { }
                }

                if (sqliteAssembly != null)
                {
                    Type baseSqliteRepository = null;
                    var candidateTypeNames = new[] {
                        "Emby.Sqlite.BaseSqliteRepository",
                        "MediaBrowser.Sqlite.BaseSqliteRepository",
                        "Emby.Server.Implementations.Data.BaseSqliteRepository"
                    };
                    foreach (var typeName in candidateTypeNames)
                    {
                        baseSqliteRepository = sqliteAssembly.GetType(typeName);
                        if (baseSqliteRepository != null) break;
                    }

                    if (baseSqliteRepository != null)
                    {
                        _createConnection = baseSqliteRepository.GetMethod("CreateConnection",
                            BindingFlags.NonPublic | BindingFlags.Instance,
                            null,
                            new[] { typeof(bool), typeof(CancellationToken) },
                            null);
                        if (_createConnection == null)
                        {
                            _createConnection = baseSqliteRepository.GetMethod("CreateConnection",
                                BindingFlags.NonPublic | BindingFlags.Instance,
                                null,
                                new[] { typeof(bool) },
                                null);
                        }

                        _dbFilePath = baseSqliteRepository.GetProperty("DbFilePath", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (_dbFilePath == null)
                        {
                            _dbFilePath = baseSqliteRepository.GetProperty("DbFilePath", BindingFlags.Public | BindingFlags.Instance);
                        }
                    }
                }
            }
            catch { }

            try
            {
                var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
                if (embyServerImplementationsAssembly != null)
                {
                    var sqliteItemRepository = embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.Data.SqliteItemRepository");
                    if (sqliteItemRepository != null)
                    {
                        Type[][] candidateParamTypes = new[]
                        {
                            new[] { typeof(InternalItemsQuery), typeof(List<KeyValuePair<string, string>>), typeof(string) },
                            new[] { typeof(InternalItemsQuery), typeof(List<KeyValuePair<string, string>>), typeof(string), typeof(string), typeof(bool) }
                        };
                        foreach (var paramTypes in candidateParamTypes)
                        {
                            _getJoinCommandText = sqliteItemRepository.GetMethod(
                                "GetJoinCommandText",
                                BindingFlags.NonPublic | BindingFlags.Instance,
                                null,
                                paramTypes,
                                null);
                            if (_getJoinCommandText != null) break;
                        }

                        _createSearchTerm = sqliteItemRepository.GetMethod("CreateSearchTerm", BindingFlags.NonPublic | BindingFlags.Static);
                        _cacheIdsFromTextParams = sqliteItemRepository.GetMethod("CacheIdsFromTextParams", BindingFlags.Instance | BindingFlags.NonPublic);
                    }
                }
            }
            catch { }
        }

        protected override void Prepare(bool apply)
        {
            // No action needed
        }

        [HarmonyPrefix]
        private static void CreateNewConnectionPrefix(ref bool isReadOnly)
        {
            if (isReadOnly)
            {
                isReadOnly = false;
            }
        }

        private static bool CreateConnectionPostfixPlatform()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Environment.Is64BitProcess)
            {
                return PatchUnpatch(
                    Instance.PatchTracker,
                    true,
                    _createConnection,
                    prefix: nameof(CreateNewConnectionPrefix),
                    postfix: nameof(CreateConnectionPostfixWin)
                    );
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (RuntimeInformation.ProcessArchitecture == Architecture.X64 ||
                    RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                {
                    return PatchUnpatch(
                        Instance.PatchTracker,
                        true,
                        _createConnection,
                        prefix: nameof(CreateNewConnectionPrefix),
                        postfix: RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                            ? nameof(CreateConnectionPostfixLinuxArm64)
                            : nameof(CreateConnectionPostfixLinux)
                        );
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return PatchUnpatch(
                    Instance.PatchTracker,
                    true,
                    _createConnection,
                    prefix: nameof(CreateNewConnectionPrefix),
                    postfix: nameof(CreateConnectionPostfixOsx)
                    );
            }

            return false;
        }

        private static void PatchPhase1()
        {
            if (EnsureTokenizerExists() && CreateConnectionPostfixPlatform()) return;

            if (Plugin.Instance.DebugMode)
            {
                Plugin.Instance.Logger.Debug("EnhanceChineseSearch - PatchPhase1 Failed");
            }

            ResetOptions();
        }


        private static void PatchPhase2(IDatabaseConnection connection)
        {
            var tokenizerCheckQuery = $@"
                SELECT 
                    CASE 
                        WHEN instr(sql, 'tokenize=""{SimpleTokenizerName}""') > 0 THEN '{SimpleTokenizerName}'
                        WHEN instr(sql, 'tokenize=""{StockTokenizerName}""') > 0 THEN '{StockTokenizerName}'
                        ELSE 'unknown'
                    END AS tokenizer_name
                FROM 
                    sqlite_master 
                WHERE 
                    type = 'table' AND 
                    name = '{FtsTableName}';";

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
                        if (string.Equals(CurrentTokenizerName, SimpleTokenizerName, StringComparison.Ordinal))
                        {
                            rebuildFtsResult = RebuildFts(connection, StockTokenizerName);
                        }
                        if (rebuildFtsResult)
                        {
                            CurrentTokenizerName = StockTokenizerName;
                            Plugin.Instance.Logger.Info("EnhanceChineseSearch - Restore Success");
                        }
                        ResetOptions();
                    }
                    else if (Plugin.Instance.MainOptionsStore.GetOptions().ModOptions.EnhanceChineseSearch)
                    {
                        patchSearchFunctionsResult = PatchSearchFunctions();

                        if (patchSearchFunctionsResult)
                        {
                            if (string.Equals(CurrentTokenizerName, StockTokenizerName, StringComparison.Ordinal))
                            {
                                rebuildFtsResult = RebuildFts(connection, SimpleTokenizerName);
                            }

                            if (rebuildFtsResult)
                            {
                                CurrentTokenizerName = SimpleTokenizerName;
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
                    Plugin.Instance.Logger.Debug("EnhanceChineseSearch - PatchPhase2 Failed");
                    Plugin.Instance.Logger.Debug(e.Message);
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                }
            }

            if (!patchSearchFunctionsResult || !rebuildFtsResult ||
                string.Equals(CurrentTokenizerName, "unknown", StringComparison.Ordinal))
            {
                ResetOptions();
            }
        }

        private static bool RebuildFts(IDatabaseConnection connection, string tokenizerName)
        {
            string populateQuery;

            if (AppVer < Ver4900)
            {
                populateQuery =
                    $"insert into {FtsTableName}(RowId, Name, OriginalTitle, SeriesName, Album) select id, " +
                    GetSearchColumnNormalization("Name") + ", " +
                    GetSearchColumnNormalization("OriginalTitle") + ", " +
                    GetSearchColumnNormalization("SeriesName") + ", " +
                    GetSearchColumnNormalization("Album") +
                    " from MediaItems";
            }
            else
            {
                populateQuery =
                    $"insert into {FtsTableName}(RowId, Name, OriginalTitle, SeriesName, Album) select id, " +
                    GetSearchColumnNormalization("Name") + ", " +
                    GetSearchColumnNormalization("OriginalTitle") + ", " +
                    GetSearchColumnNormalization("SeriesName") + ", " +
                    GetSearchColumnNormalization(
                        "(select case when AlbumId is null then null else (select name from MediaItems where Id = AlbumId limit 1) end)") +
                    " from MediaItems";
            }

            connection.BeginTransaction(TransactionMode.Immediate);
            try
            {
                var dropFtsTableQuery = $"DROP TABLE IF EXISTS {FtsTableName}";
                connection.Execute(dropFtsTableQuery);

                var prefix = string.Equals(tokenizerName, SimpleTokenizerName) ? "" : ", prefix='1 2 3 4'";
                var createFtsTableQuery =
                    $"CREATE VIRTUAL TABLE IF NOT EXISTS {FtsTableName} USING FTS5 (Name, OriginalTitle, SeriesName, Album, tokenize=\"{tokenizerName}\"{prefix})";
                connection.Execute(createFtsTableQuery);

                Plugin.Instance.Logger.Info($"EnhanceChineseSearch - Filling {FtsTableName} Start");
                connection.Execute(populateQuery);
                connection.CommitTransaction();
                Plugin.Instance.Logger.Info($"EnhanceChineseSearch - Filling {FtsTableName} Complete");

                return true;
            }
            catch (Exception e)
            {
                connection.RollbackTransaction();
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug("EnhanceChineseSearch - RebuildFts Failed");
                    Plugin.Instance.Logger.Debug(e.Message);
                    Plugin.Instance.Logger.Debug(e.StackTrace);
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
                if (File.Exists(TokenizerPath))
                {
                    var existingSha1 = ComputeSha1(TokenizerPath);

                    if (expectedSha1.ContainsValue(existingSha1))
                    {
                        var highestVersion = expectedSha1.Keys.Max();
                        var highestSha1 = expectedSha1[highestVersion];

                        if (existingSha1 == highestSha1)
                        {
                            Plugin.Instance.Logger.Info(
                                $"EnhanceChineseSearch - Tokenizer exists with matching SHA-1 for the highest version {highestVersion}");
                            return true;
                        }

                        var currentVersion = expectedSha1.FirstOrDefault(x => x.Value == existingSha1).Key;
                        Plugin.Instance.Logger.Info(
                            $"EnhanceChineseSearch - Tokenizer exists for version {currentVersion} but does not match the highest version {highestVersion}. Upgrading...");
                    }
                    else
                    {
                        Plugin.Instance.Logger.Info(
                            "EnhanceChineseSearch - Tokenizer exists but SHA-1 is not recognized. Overwriting...");
                    }
                }
                else
                {
                    Plugin.Instance.Logger.Info("EnhanceChineseSearch - Tokenizer does not exist. Exporting...");
                }

                ExportTokenizer(resourceName);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Debug("EnhanceChineseSearch - EnsureTokenizerExists Failed");
                Plugin.Instance.Logger.Debug(e.Message);

                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                }
            }

            return false;
        }

        private static void ExportTokenizer(string resourceName)
        {
            try
            {
                var directory = Path.GetDirectoryName(TokenizerPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using var resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
                using var fileStream = new FileStream(TokenizerPath, FileMode.Create, FileAccess.Write);
                resourceStream.CopyTo(fileStream);

                Plugin.Instance.Logger.Info($"EnhanceChineseSearch - Exported {resourceName} to {TokenizerPath}");

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    try
                    {
                        var process = new System.Diagnostics.Process
                        {
                            StartInfo = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "chmod",
                                Arguments = $"+x \"{TokenizerPath}\"",
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true
                            }
                        };
                        process.Start();
                        process.WaitForExit(5000);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static string GetTokenizerResourceName()
        {
            if (!Environment.Is64BitOperatingSystem) return null;

            var tokenizerNamespace = Assembly.GetExecutingAssembly().GetName().Name + ".Tokenizer";
            string platformPart = null;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Environment.Is64BitProcess)
            {
                platformPart = "win_x64";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                {
                    platformPart = "linux_x64";
                }
                else if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                {
                    platformPart = "linux_arm64";
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                platformPart = "osx_universal";
            }

            if (platformPart == null) return null;
            return $"{tokenizerNamespace}.{platformPart}.libsimple.so";
        }

        private static Dictionary<Version, string> GetExpectedSha1()
        {
            if (!Environment.Is64BitOperatingSystem) return null;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new Dictionary<Version, string>
                {
                    { new Version(0, 5, 2), "e99315d7005c6b04b55a5f71e45eb3aac41670ba" }
                };
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                {
                    return new Dictionary<Version, string>
                    {
                        { new Version(0, 5, 2), "b1650dd9348e7242a2908eee1f998d510564d4e7" }
                    };
                }
                else if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                {
                    return new Dictionary<Version, string>
                    {
                        { new Version(0, 5, 2), "acb77b9ea2b823a1a4d8383ce501271cfb27ba19" }
                    };
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return new Dictionary<Version, string>
                {
                    { new Version(0, 5, 2), "20a299e89ecf02dd75d2835c922c53c6cca133d8" }
                };
            }

            return null;
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
            bool patchedJoinCommand = false, patchedSearchTerm = false, patchedCacheIds = false;

            if (_getJoinCommandText != null)
            {
                patchedJoinCommand = PatchUnpatch(Instance.PatchTracker, true, _getJoinCommandText, postfix: nameof(GetJoinCommandTextPostfix));
            }

            if (_createSearchTerm != null)
            {
                patchedSearchTerm = PatchUnpatch(Instance.PatchTracker, true, _createSearchTerm, prefix: nameof(CreateSearchTermPrefix));
            }

            if (_cacheIdsFromTextParams != null)
            {
                patchedCacheIds = PatchUnpatch(Instance.PatchTracker, true, _cacheIdsFromTextParams, prefix: nameof(CacheIdsFromTextParamsPrefix));
            }

            bool result = patchedJoinCommand && patchedSearchTerm && patchedCacheIds;
            if (result)
            {
                _searchPatchCompleted = true;
            }
            return result;
        }

        private static bool LoadTokenizerExtension(IDatabaseConnection connection)
        {
            try
            {
                if (!File.Exists(TokenizerPath) || _sqlite3_enable_load_extension == null || _sqlite3_db == null)
                {
                    return false;
                }

                var dbObj = _sqlite3_db.GetValue(connection);
                if (dbObj == null || dbObj.GetType().FullName != "SQLitePCLEx.sqlite3")
                {
                    return false;
                }

                _sqlite3_enable_load_extension.Invoke(_sqlitePCLRawExRaw, new object[] { dbObj, 1 });
                var escapedPath = TokenizerPath.Replace("\\", "\\\\");
                connection.Execute($"SELECT load_extension('{escapedPath}')");

                return true;
            }
            catch (TargetInvocationException tie)
            {
                Plugin.Instance.Logger.Warn("EnhanceChineseSearch - Load tokenizer failed (TargetInvocationException).");

                var inner = tie.InnerException ?? tie;
                Plugin.Instance.Logger.Debug(inner.GetType().FullName);
                Plugin.Instance.Logger.Debug(inner.Message);
                Plugin.Instance.Logger.Debug(inner.StackTrace);

                Instance.PatchTracker.FallbackPatchApproach = PatchApproach.None;
                return false;
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Warn("EnhanceChineseSearch - Load tokenizer failed.");

                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug(e.GetType().FullName);
                    Plugin.Instance.Logger.Debug(e.Message);
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                }

                Instance.PatchTracker.FallbackPatchApproach = PatchApproach.None;
                return false;
            }

            return false;
        }

        [HarmonyPostfix]
        private static void CreateConnectionPostfixLinux(object __instance, [HarmonyArgument("isReadOnly")] bool isReadOnly,
            [HarmonyArgument("cancellationToken")] CancellationToken _, ref IDatabaseConnection __result)
        {
            CreateConnectionPostfixCommon(__instance, isReadOnly, __result);
        }

        [HarmonyPostfix]
        private static void CreateConnectionPostfixLinuxArm64(object __instance, [HarmonyArgument("isReadOnly")] bool isReadOnly,
            [HarmonyArgument("cancellationToken")] CancellationToken _, ref IDatabaseConnection __result)
        {
            CreateConnectionPostfixCommon(__instance, isReadOnly, __result);
        }

        [HarmonyPostfix]
        private static void CreateConnectionPostfixOsx(object __instance, [HarmonyArgument("isReadOnly")] bool isReadOnly,
            [HarmonyArgument("cancellationToken")] CancellationToken _, ref IDatabaseConnection __result)
        {
            CreateConnectionPostfixCommon(__instance, isReadOnly, __result);
        }

        private static void CreateConnectionPostfixCommon(object __instance, bool isReadOnly, IDatabaseConnection __result)
        {
            if (_searchPatchCompleted || isReadOnly || _patchPhase2Initialized) return;

            lock (_lock)
            {
                if (_patchPhase2Initialized) return;

                var dbPath = _dbFilePath?.GetValue(__instance) as string;
                if (dbPath?.EndsWith("library.db", StringComparison.OrdinalIgnoreCase) != true)
                {
                    return;
                }

                var tokenizerLoaded = LoadTokenizerExtension(__result);
                if (tokenizerLoaded)
                {
                    _patchPhase2Initialized = true;
                    PatchPhase2(__result);
                }
            }
        }

        [HarmonyPostfix]
        private static void CreateConnectionPostfixWin(object __instance, [HarmonyArgument("isReadOnly")] bool isReadOnly,
    ref IDatabaseConnection __result)
        {
            if (_searchPatchCompleted || isReadOnly || _patchPhase2Initialized) return;

            lock (_lock)
            {
                if (_patchPhase2Initialized) return;

                var dbPath = _dbFilePath?.GetValue(__instance) as string;
                if (dbPath?.EndsWith("library.db", StringComparison.OrdinalIgnoreCase) != true)
                {
                    return;
                }

                var tokenizerLoaded = LoadTokenizerExtension(__result);
                if (tokenizerLoaded)
                {
                    _patchPhase2Initialized = true;
                    PatchPhase2(__result);
                }
            }
        }

        [HarmonyPostfix]
        private static void GetJoinCommandTextPostfix(
            InternalItemsQuery query,
            List<KeyValuePair<string, string>> bindParams,
            ref StringBuilder __result)
        {
            var sql = __result.ToString();
            var newSql = sql;

            bool hasMatchParam =
              newSql.Contains("match @SearchTerm", StringComparison.OrdinalIgnoreCase)
              || Regex.IsMatch(newSql, @"\bmatch\b\s*\(?\s*@SearchTerm\b", RegexOptions.IgnoreCase);

            if (!string.IsNullOrEmpty(query.SearchTerm) && hasMatchParam)
            {
                var replacement = Plugin.Instance.MainOptionsStore.GetOptions().ModOptions.ExcludeOriginalTitleFromSearch
                    ? "match '-OriginalTitle:' || simple_query(@SearchTerm)"
                    : "match simple_query(@SearchTerm)";

                newSql = Regex.Replace(
                    newSql,
                    @"\bmatch\b\s*\(?\s*@SearchTerm\b",
                    replacement,
                    RegexOptions.IgnoreCase
                );
            }

            if (!string.IsNullOrEmpty(query.Name) && hasMatchParam)
            {
                newSql = Regex.Replace(
                    newSql,
                    @"\bmatch\b\s*\(?\s*@SearchTerm\b",
                    "match 'Name:' || simple_query(@SearchTerm)",
                    RegexOptions.IgnoreCase
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
                           [(currentValue.IndexOf(":", StringComparison.Ordinal) + 1)..]
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
                    foreach (var provider in ProviderPatterns)
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
                    _ = LoadTokenizerExtension(db);
                }
            }

            return true;
        }
    }
}