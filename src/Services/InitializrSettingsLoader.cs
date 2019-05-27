﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Abstractions.Mount;
using Microsoft.TemplateEngine.Abstractions.PhysicalFileSystem;
using Microsoft.TemplateEngine.Edge;
using Microsoft.TemplateEngine.Edge.Mount.FileSystem;
using Microsoft.TemplateEngine.Edge.Settings;
using Microsoft.TemplateEngine.Utils;
using Newtonsoft.Json.Linq;


namespace InitializrApi.Services
{
     public class InitializrSettingsLoader : ISettingsLoader
        {
            public static readonly Guid FactoryId = new Guid("8C19221B-DEA3-4250-86FE-2D4E189A11D2");
            public static readonly Guid ZipFactoryId = new Guid("94E92610-CF4C-4F6D-AEB6-9E42DDE1899D");

            private const int MaxLoadAttempts = 20;
            public static readonly string HostTemplateFileConfigBaseName = ".host.json";

            private SettingsStore _userSettings;
            private TemplateCache _userTemplateCache;
            private IMountPointManager _mountPointManager;
            private IComponentManager _componentManager;
            private bool _isLoaded;
            private Dictionary<Guid, MountPointInfo> _mountPoints;
            private bool _templatesLoaded;
            private InstallUnitDescriptorCache _installUnitDescriptorCache;
            private bool _installUnitDescriptorsLoaded;
            private readonly Paths _paths;
            private readonly string _hivePath;

            public InitializrSettingsLoader(IEngineEnvironmentSettings environmentSettings, string hivePath)
            {
                EnvironmentSettings = environmentSettings;
                _paths = new Paths(environmentSettings);
                _userTemplateCache = new TemplateCache(environmentSettings);
                _installUnitDescriptorCache = new InstallUnitDescriptorCache(environmentSettings);
                _hivePath = hivePath;
            }

            public void Save()
            {
                Save(_userTemplateCache);
            }

            private void Save(TemplateCache cacheToSave)
            {
                // When writing the template caches, we need the existing cache version to read the existing caches for before updating.
                // so don't update it until after the template caches are written.

             //   cacheToSave.WriteTemplateCaches(_userSettings.Version);

                // now it's safe to update the cache version, which is written in the settings file.
                _userSettings.SetVersionToCurrent();
                JObject serialized = JObject.FromObject(_userSettings);
                _paths.WriteAllText(_paths.User.SettingsFile, serialized.ToString());

                WriteInstallDescriptorCache();

                if (_userTemplateCache != cacheToSave)  // object equals
                {
                    ReloadTemplates();
                }
            }

            public TemplateCache UserTemplateCache
            {
                get
                {
                    EnsureLoaded();
                    return _userTemplateCache;
                }
            }

            // It's important to note that these are loaded on demand, not at initialization of SettingsLoader.
            // So the backing field shouldn't be directly accessed except during initialization.
            public InstallUnitDescriptorCache InstallUnitDescriptorCache
            {
                get
                {
                    EnsureLoaded();
                    EnsureInstallDescriptorsLoaded();

                    return _installUnitDescriptorCache;
                }
            }

            private void EnsureInstallDescriptorsLoaded()
            {
                if (_installUnitDescriptorsLoaded)
                {
                    return;
                }

                string descriptorFileContents = _paths.ReadAllText(_paths.User.InstallUnitDescriptorsFile, "{}");
                JObject parsed = JObject.Parse(descriptorFileContents);

                _installUnitDescriptorCache = InstallUnitDescriptorCache.FromJObject(EnvironmentSettings, parsed);
                _installUnitDescriptorsLoaded = true;
            }

            // Write the install unit descriptors.
            // Get them from the property to ensure they're loaded. Descriptors are loaded on demand, not at startup.
            private void WriteInstallDescriptorCache()
            {
                JObject installDescriptorsSerialized = JObject.FromObject(InstallUnitDescriptorCache);
                _paths.WriteAllText(_paths.User.InstallUnitDescriptorsFile, installDescriptorsSerialized.ToString());
            }

            private void EnsureLoaded()
            {
                if (_isLoaded)
                {
                    return;
                }

                string userSettings = null;
                using (Timing.Over(EnvironmentSettings.Host, "Read settings"))
                    for (int i = 0; i < MaxLoadAttempts; ++i)
                    {
                        try
                        {
                            userSettings = _paths.ReadAllText(_paths.User.SettingsFile, "{}");
                            break;
                        }
                        catch (IOException)
                        {
                            if (i == MaxLoadAttempts - 1)
                            {
                                throw;
                            }

                            Task.Delay(2).Wait();
                        }
                    }
                JObject parsed;
                using (Timing.Over(EnvironmentSettings.Host, "Parse settings"))
                    try
                    {
                        parsed = JObject.Parse(userSettings);
                    }
                    catch (Exception ex)
                    {
                        throw new EngineInitializationException("Error parsing the user settings file", "Settings File", ex);
                    }
                    using (Timing.Over(EnvironmentSettings.Host, "Deserialize user settings"))
                    
                        _userSettings = new SettingsStore(parsed);

                        ////Hack the path in here
                        //foreach (var mountPoint in _userSettings.MountPoints)
                        //{
                        //    if (mountPoint.Place.Contains("__Path__"))
                        //    {
                        //mountPoint.Place=  mountPoint.Place.Replace("__Path__", Path.Combine(_hivePath, "/"));
                        //    }
                    

                using (Timing.Over(EnvironmentSettings.Host, "Init probing paths"))
                    if (_userSettings.ProbingPaths.Count == 0)
                    {
                        _userSettings.ProbingPaths.Add(_paths.User.Content);
                    }

                _mountPoints = new Dictionary<Guid, MountPointInfo>();
                using (Timing.Over(EnvironmentSettings.Host, "Load mount points"))
                    foreach (MountPointInfo info in _userSettings.MountPoints)
                    {
                        _mountPoints[info.MountPointId] = info;
                    }

                using (Timing.Over(EnvironmentSettings.Host, "Init Component manager"))
                    _componentManager = new InitializrComponentManager(this, _userSettings);
                using (Timing.Over(EnvironmentSettings.Host, "Init Mount Point manager"))
                    _mountPointManager = new InitializrMountPointManager(EnvironmentSettings, _componentManager);

                using (Timing.Over(EnvironmentSettings.Host, "Demand template load"))
                    EnsureTemplatesLoaded();

                _isLoaded = true;
            }

            // Loads from the template cache
            private void EnsureTemplatesLoaded()
            {
                if (_templatesLoaded)
                {
                    return;
                }

                string userTemplateCache;

                if (_paths.Exists(_paths.User.CurrentLocaleTemplateCacheFile))
                {
                    using (Timing.Over(EnvironmentSettings.Host, "Read template cache"))
                        userTemplateCache = _paths.ReadAllText(_paths.User.CurrentLocaleTemplateCacheFile, "{}");
                }
                else if (_paths.Exists(_paths.User.CultureNeutralTemplateCacheFile))
                {
                    // clone the culture neutral cache
                    // this should not occur if there are any langpacks installed for this culture.
                    // when they got installed, the cache should have been created for that locale.
                    using (Timing.Over(EnvironmentSettings.Host, "Clone cultural neutral cache"))
                    {
                        userTemplateCache = _paths.ReadAllText(_paths.User.CultureNeutralTemplateCacheFile, "{}");
                        _paths.WriteAllText(_paths.User.CurrentLocaleTemplateCacheFile, userTemplateCache);
                    }
                }
                else
                {
                    userTemplateCache = "{}";
                }

                JObject parsed;
                using (Timing.Over(EnvironmentSettings.Host, "Parse template cache"))
                    parsed = JObject.Parse(userTemplateCache);
                using (Timing.Over(EnvironmentSettings.Host, "Init template cache"))
                    _userTemplateCache = new TemplateCache(EnvironmentSettings, parsed, _userSettings.Version);

                _templatesLoaded = true;
            }

            public void Reload()
            {
                _isLoaded = false;
                EnsureLoaded();
            }

            private void UpdateTemplateListFromCache(TemplateCache cache, ISet<ITemplateInfo> templates)
            {
                using (Timing.Over(EnvironmentSettings.Host, "Enumerate infos"))
                    templates.UnionWith(cache.TemplateInfo);
            }

            public void RebuildCacheFromSettingsIfNotCurrent(bool forceRebuild)
            {
                EnsureLoaded();

                MountPointInfo[] mountPointsToScan = FindMountPointsToScan(forceRebuild).ToArray();

                if (!mountPointsToScan.Any())
                {
                    // Nothing to do
                    return;
                }

                TemplateCache workingCache = new TemplateCache(EnvironmentSettings);
                foreach (MountPointInfo mountPoint in mountPointsToScan)
                {
                    workingCache.Scan(mountPoint.Place);
                }

                Save(workingCache);

                ReloadTemplates();
            }

            private IEnumerable<MountPointInfo> FindMountPointsToScan(bool forceRebuild)
            {
                // If the user settings version is out of date, or
                // we've been asked to rebuild everything then
                // we need to scan everything
                bool forceScanAll = !IsVersionCurrent || forceRebuild;

                // load up the culture neutral cache
                // and get the mount points for templates from the culture neutral cache
                HashSet<TemplateInfo> allTemplates = new HashSet<TemplateInfo>(_userTemplateCache.GetTemplatesForLocale(null, _userSettings.Version));

                // loop through the localized caches and get all the locale mount points
                foreach (string locale in _userTemplateCache.AllLocalesWithCacheFiles)
                {
                    allTemplates.UnionWith(_userTemplateCache.GetTemplatesForLocale(locale, _userSettings.Version));
                }

                foreach (TemplateInfo template in allTemplates)
                {
                    if (!_mountPoints.TryGetValue(template.ConfigMountPointId, out MountPointInfo mountPoint))
                    {
                        // TODO: This should never happen - throw an error?
                        continue;
                    }
                    if (forceScanAll)
                    {
                        yield return mountPoint;
                        continue;
                    }

                    // For MountPoints using FileSystemMountPointFactories
                    // we scan the file system to see if the template
                    // is more recent than our cached version
                    if (mountPoint.MountPointFactoryId != InitializrSettingsLoader.FactoryId)
                    {
                        continue;
                    }

                    string pathToTemplateFile = Path.Combine(mountPoint.Place, template.ConfigPlace.TrimStart('/'));

                    DateTime? timestampOnDisk = null;
                    if (EnvironmentSettings.Host.FileSystem is IFileLastWriteTimeSource timeSource)
                    {
                        timestampOnDisk = timeSource.GetLastWriteTimeUtc(pathToTemplateFile);
                    }

                    if (!template.ConfigTimestampUtc.HasValue
                        || (timestampOnDisk.HasValue && template.ConfigTimestampUtc.Value < timestampOnDisk))
                    {
                        // Template on disk is more recent
                        yield return mountPoint;
                    }
                }
            }

            private void ReloadTemplates()
            {
                _templatesLoaded = false;
                EnsureTemplatesLoaded();
            }

            public bool IsVersionCurrent
            {
                get
                {
                    if (string.IsNullOrEmpty(_userSettings.Version) || !string.Equals(_userSettings.Version, "v1.0", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    return true;
                }
            }

            public ITemplate LoadTemplate(ITemplateInfo info, string baselineName)
            {
                IGenerator generator;
                if (!Components.TryGetComponent(info.GeneratorId, out generator))
                {
                    return null;
                }

                IMountPoint mountPoint;
                if (!_mountPointManager.TryDemandMountPoint(info.ConfigMountPointId, out mountPoint))
                {
                    return null;
                }
                IFileSystemInfo config = mountPoint.FileSystemInfo(info.ConfigPlace);

                IFileSystemInfo localeConfig = null;
                if (!string.IsNullOrEmpty(info.LocaleConfigPlace)
                        && info.LocaleConfigMountPointId != null
                        && info.LocaleConfigMountPointId != Guid.Empty)
                {
                    IMountPoint localeMountPoint;
                    if (!_mountPointManager.TryDemandMountPoint(info.LocaleConfigMountPointId, out localeMountPoint))
                    {
                        // TODO: decide if we should proceed without loc info, instead of bailing.
                        return null;
                    }

                    localeConfig = localeMountPoint.FileSystemInfo(info.LocaleConfigPlace);
                }

                IFile hostTemplateConfigFile = FindBestHostTemplateConfigFile(config);

                ITemplate template;
                using (Timing.Over(EnvironmentSettings.Host, "Template from config"))
                    if (generator.TryGetTemplateFromConfigInfo(config, out template, localeConfig, hostTemplateConfigFile, baselineName))
                    {
                        return template;
                    }
                    else
                    {
                        //TODO: Log the failure to read the template info
                    }

                return null;
            }

            public IFile FindBestHostTemplateConfigFile(IFileSystemInfo config)
            {
                IDictionary<string, IFile> allHostFilesForTemplate = new Dictionary<string, IFile>();

                foreach (IFile hostFile in config.Parent.EnumerateFiles($"*{HostTemplateFileConfigBaseName}", SearchOption.TopDirectoryOnly))
                {
                    allHostFilesForTemplate.Add(hostFile.Name, hostFile);
                }

                string preferredHostFileName = string.Concat(EnvironmentSettings.Host.HostIdentifier, HostTemplateFileConfigBaseName);
                if (allHostFilesForTemplate.TryGetValue(preferredHostFileName, out IFile preferredHostFile))
                {
                    return preferredHostFile;
                }

                foreach (string fallbackHostName in EnvironmentSettings.Host.FallbackHostTemplateConfigNames)
                {
                    string fallbackHostFileName = string.Concat(fallbackHostName, HostTemplateFileConfigBaseName);

                    if (allHostFilesForTemplate.TryGetValue(fallbackHostFileName, out IFile fallbackHostFile))
                    {
                        return fallbackHostFile;
                    }
                }

                return null;
            }

            public IComponentManager Components
            {
                get
                {
                    EnsureLoaded();
                    return _componentManager;
                }
            }

            public IEnumerable<MountPointInfo> MountPoints
            {
                get
                {
                    EnsureLoaded();
                    return _mountPoints.Values;
                }
            }

        public IEngineEnvironmentSettings EnvironmentSettings { get; }

        public void GetTemplates(HashSet<ITemplateInfo> templates)
            {
                using (Timing.Over(EnvironmentSettings.Host, "Settings init"))
                    EnsureLoaded();
                using (Timing.Over(EnvironmentSettings.Host, "Template load"))
                    UpdateTemplateListFromCache(_userTemplateCache, templates);
            }

            public void WriteTemplateCache(IList<ITemplateInfo> templates, string locale)
            {
                WriteTemplateCache(templates, locale, true);
            }

            public void WriteTemplateCache(IList<ITemplateInfo> templates, string locale, bool hasContentChanges)
            {
                List<TemplateInfo> toCache = templates.Cast<TemplateInfo>().ToList();
                bool hasMountPointChanges = false;

                for (int i = 0; i < toCache.Count; ++i)
                {
                    if (!_mountPoints.ContainsKey(toCache[i].ConfigMountPointId))
                    {
                        toCache.RemoveAt(i);
                        --i;
                        hasMountPointChanges = true;
                        continue;
                    }

                    if (!_mountPoints.ContainsKey(toCache[i].HostConfigMountPointId))
                    {
                        toCache[i].HostConfigMountPointId = Guid.Empty;
                        toCache[i].HostConfigPlace = null;
                        hasMountPointChanges = true;
                    }

                    if (!_mountPoints.ContainsKey(toCache[i].LocaleConfigMountPointId))
                    {
                        toCache[i].LocaleConfigMountPointId = Guid.Empty;
                        toCache[i].LocaleConfigPlace = null;
                        hasMountPointChanges = true;
                    }
                }

                if (hasContentChanges || hasMountPointChanges)
                {
                    TemplateCache cache = new TemplateCache(EnvironmentSettings, toCache);
                    JObject serialized = JObject.FromObject(cache);
                    _paths.WriteAllText(_paths.User.ExplicitLocaleTemplateCacheFile(locale), serialized.ToString());
                }

                bool isCurrentLocale = string.IsNullOrEmpty(locale)
                    && string.IsNullOrEmpty(EnvironmentSettings.Host.Locale)
                    || (locale == EnvironmentSettings.Host.Locale);

                // TODO: determine if this reload is necessary if there wasn't a save (probably not needed)
                if (isCurrentLocale)
                {
                    ReloadTemplates();
                }
            }

            public void AddProbingPath(string probeIn)
            {
                const int maxAttempts = 10;
                int attemptCount = 0;
                bool successfulWrite = false;

                EnsureLoaded();
                while (!successfulWrite && attemptCount++ < maxAttempts)
                {
                    if (!_userSettings.ProbingPaths.Add(probeIn))
                    {
                        return;
                    }

                    try
                    {
                        Save();
                        successfulWrite = true;
                    }
                    catch
                    {
                        Task.Delay(10).Wait();
                        Reload();
                    }
                }
            }

            public bool TryGetMountPointInfo(Guid mountPointId, out MountPointInfo info)
            {
                EnsureLoaded();
                using (Timing.Over(EnvironmentSettings.Host, "Mount point lookup"))
                    return _mountPoints.TryGetValue(mountPointId, out info);
            }

            public bool TryGetMountPointInfoFromPlace(string mountPointPlace, out MountPointInfo info)
            {
                EnsureLoaded();
                using (Timing.Over(EnvironmentSettings.Host, "Mount point place lookup"))
                    foreach (MountPointInfo mountInfoToCheck in _mountPoints.Values)
                    {
                        if (mountPointPlace.Equals(mountInfoToCheck.Place, StringComparison.OrdinalIgnoreCase))
                        {
                            info = mountInfoToCheck;
                            return true;
                        }
                    }

                info = null;
                return false;
            }

            public bool TryGetMountPointFromPlace(string mountPointPlace, out IMountPoint mountPoint)
            {
                if (!TryGetMountPointInfoFromPlace(mountPointPlace, out MountPointInfo info))
                {
                    mountPoint = null;
                    return false;
                }

                return _mountPointManager.TryDemandMountPoint(info.MountPointId, out mountPoint);
            }

            public void AddMountPoint(IMountPoint mountPoint)
            {
                if (_mountPoints.Values.Any(x => string.Equals(x.Place, mountPoint.Info.Place) && x.ParentMountPointId == mountPoint.Info.ParentMountPointId))
                {
                    return;
                }

                _mountPoints[mountPoint.Info.MountPointId] = mountPoint.Info;
                _userSettings.MountPoints.Add(mountPoint.Info);
                JObject serialized = JObject.FromObject(_userSettings);
                _paths.WriteAllText(_paths.User.SettingsFile, serialized.ToString());
            }

            public bool TryGetFileFromIdAndPath(Guid mountPointId, string place, out IFile file, out IMountPoint mountPoint)
            {
                EnsureLoaded();
                if (!string.IsNullOrEmpty(place) && _mountPointManager.TryDemandMountPoint(mountPointId, out mountPoint))
                {
                    file = mountPoint.FileInfo(place);
                    return file != null && file.Exists;
                }

                mountPoint = null;
                file = null;
                return false;
            }

            public bool TryGetMountPointFromId(Guid mountPointId, out IMountPoint mountPoint)
            {
                return _mountPointManager.TryDemandMountPoint(mountPointId, out mountPoint);
            }

            public void RemoveMountPoints(IEnumerable<Guid> mountPoints)
            {
                foreach (Guid g in mountPoints)
                {
                    if (_mountPoints.TryGetValue(g, out MountPointInfo info))
                    {
                        _userSettings.MountPoints.Remove(info);
                        _mountPoints.Remove(g);
                    }
                }
            }

            public void ReleaseMountPoint(IMountPoint mountPoint)
            {
                _mountPointManager.ReleaseMountPoint(mountPoint);
            }

            public void RemoveMountPoint(IMountPoint mountPoint)
            {
                _mountPointManager.ReleaseMountPoint(mountPoint);
                RemoveMountPoints(new[] { mountPoint.Info.MountPointId });
            }
        }
    }

