﻿using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bonsai.NuGet
{
    public class PackageManager : IPackageManager
    {
        public PackageManager(PackageSourceProvider packageSourceProvider, string path)
            : this(packageSourceProvider.Settings, packageSourceProvider, path)
        {
        }

        public PackageManager(ISettings settings, IPackageSourceProvider packageSourceProvider, string path)
            : this(settings, packageSourceProvider, new PackageSource(path))
        {
        }

        public PackageManager(ISettings settings, IPackageSourceProvider packageSourceProvider, PackageSource localRepository, PackagePathResolver pathResolver = null)
        {
            if (packageSourceProvider == null) throw new ArgumentNullException(nameof(packageSourceProvider));
            if (localRepository == null) throw new ArgumentNullException(nameof(localRepository));
            SourceRepositoryProvider = new SourceRepositoryProvider(packageSourceProvider, Repository.Provider.GetCoreV3());
            PathResolver = pathResolver ?? new PackagePathResolver(localRepository.Source);
            LocalRepository = SourceRepositoryProvider.CreateRepository(localRepository);
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            PackageManagerPlugins = new List<PackageManagerPlugin>();
            DependencyBehavior = DependencyBehavior.Highest;
            ProjectFramework = NuGetFramework.AgnosticFramework;
            PackageSaveMode = PackageSaveMode.Defaultv3;
            Logger = NullLogger.Instance;
        }

        public ILogger Logger { get; set; }

        public NuGetFramework ProjectFramework { get; set; }

        public DependencyBehavior DependencyBehavior { get; set; }

        public PackagePathResolver PathResolver { get; private set; }

        public SourceRepository LocalRepository { get; private set; }

        public ISourceRepositoryProvider SourceRepositoryProvider { get; private set; }

        public ICollection<PackageManagerPlugin> PackageManagerPlugins { get; }

        public ISettings Settings { get; private set; }

        public PackageSaveMode PackageSaveMode { get; set; }

        protected virtual bool AcceptLicenseAgreement(IEnumerable<IPackageSearchMetadata> licensePackages)
        {
            return true;
        }

        private async Task<PackageReaderBase> ExtractPackage(
            SourcePackageDependencyInfo packageInfo,
            SourceCacheContext cacheContext,
            PackageExtractionContext packageExtractionContext,
            ILogger logger,
            CancellationToken token)
        {
            logger.LogInformation($"Installing package '{packageInfo.Id} {packageInfo.Version}'.");
            var downloadResource = await packageInfo.Source.GetResourceAsync<DownloadResource>(token);
            var downloadResult = await downloadResource.GetDownloadResourceResultAsync(
                packageInfo,
                new PackageDownloadContext(cacheContext),
                SettingsUtility.GetGlobalPackagesFolder(Settings),
                logger, token);

            var installPath = PathResolver.GetInstallPath(packageInfo);
            foreach (var plugin in PackageManagerPlugins)
            {
                var accepted = await plugin.OnPackageInstallingAsync(packageInfo, downloadResult.PackageReader, installPath);
                if (!accepted) return downloadResult.PackageReader;
            }

            await PackageExtractor.ExtractPackageAsync(
                downloadResult.PackageSource,
                downloadResult.PackageStream,
                PathResolver,
                packageExtractionContext,
                token);

            foreach (var plugin in PackageManagerPlugins)
            {
                await plugin.OnPackageInstalledAsync(packageInfo, downloadResult.PackageReader, installPath);
            }
            return downloadResult.PackageReader;
        }

        static void DeleteDirectoryTree(DirectoryInfo directory)
        {
            foreach (var subdirectory in directory.GetDirectories())
            {
                DeleteDirectoryTree(subdirectory);
            }

            try { directory.Delete(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private async Task DeletePackage(LocalPackageInfo package, string installPath, ILogger logger, CancellationToken token)
        {
            logger.LogInformation($"Deleting package '{package.Identity}'.");
            using (var packageReader = package.GetReader())
            {
                foreach (var plugin in PackageManagerPlugins)
                {
                    var accepted = await plugin.OnPackageUninstallingAsync(package.Identity, packageReader, installPath);
                    if (!accepted) continue;
                }

                foreach (var file in await packageReader.GetPackageFilesAsync(PackageSaveMode, token))
                {
                    var path = Path.Combine(installPath, file);
                    FileUtility.Delete(path);
                }
            }

            FileUtility.Delete(package.Path);
            var installTree = new DirectoryInfo(installPath);
            DeleteDirectoryTree(installTree);
            foreach (var plugin in PackageManagerPlugins)
            {
                await plugin.OnPackageUninstalledAsync(package.Identity, null, installPath);
            }
        }

        public async Task<IEnumerable<LocalPackageInfo>> GetInstalledPackagesAsync(CancellationToken token)
        {
            var localPackagesResource = await LocalRepository.GetResourceAsync<FindLocalPackagesResource>(token);
            return localPackagesResource.GetPackages(NullLogger.Instance, token);
        }

        public async Task<PackageReaderBase> InstallPackageAsync(PackageIdentity package, bool ignoreDependencies, CancellationToken token)
        {
            using (var cacheContext = new SourceCacheContext())
            {
                var logger = Logger;
                var framework = ProjectFramework;
                var installedPath = PathResolver.GetInstalledPath(package);
                if (installedPath != null)
                {
                    return new PackageFolderReader(installedPath);
                }

                var repositories = SourceRepositoryProvider.GetRepositories();
                var dependencyInfoResource = await LocalRepository.GetResourceAsync<DependencyInfoResource>(token);
                var installedPackages = (await GetInstalledPackagesAsync(token)).Select(info => info.Identity);
                var localPackages = await GetDependencyInfoAsync(dependencyInfoResource, installedPackages, framework);
                var sourcePackages = localPackages.ToDictionary(dependencyInfo => dependencyInfo, PackageIdentityComparer.Default);
                var packageVersion = new VersionRange(package.Version, new FloatRange(NuGetVersionFloatBehavior.None));
                await GetPackageDependencies(package.Id, packageVersion, framework, cacheContext, repositories, sourcePackages, logger, ignoreDependencies, token);

                var resolverContext = new PackageResolverContext(
                    dependencyBehavior: ignoreDependencies ? DependencyBehavior.Ignore : DependencyBehavior,
                    targetIds: new[] { package.Id },
                    requiredPackageIds: Enumerable.Empty<string>(),
                    packagesConfig: Enumerable.Empty<PackageReference>(),
                    preferredVersions: Enumerable.Empty<PackageIdentity>(),
                    availablePackages: sourcePackages.Values,
                    packageSources: repositories.Select(repository => repository.PackageSource),
                    log: NullLogger.Instance);

                var resolver = new PackageResolver();
                var installOperations = resolver.Resolve(resolverContext, token);
                var packagesToRemove = new List<PackageIdentity>();
                var licensePackages = new List<IPackageSearchMetadata>();
                var findLocalPackageResource = await LocalRepository.GetResourceAsync<FindPackageByIdResource>(token);
                foreach (var identity in installOperations)
                {
                    installedPath = PathResolver.GetInstalledPath(identity);
                    if (installedPath == null)
                    {
                        var packageInfo = sourcePackages[identity];
                        var packageMetadataResource = await packageInfo.Source.GetResourceAsync<PackageMetadataResource>(token);
                        var packageMetadata = await packageMetadataResource.GetMetadataAsync(identity, cacheContext, NullLogger.Instance, token);
                        if (packageMetadata.RequireLicenseAcceptance) licensePackages.Add(packageMetadata);
                        try
                        {
                            var existingPackages = await findLocalPackageResource.GetAllVersionsAsync(identity.Id, cacheContext, NullLogger.Instance, token);
                            packagesToRemove.AddRange(existingPackages.Select(version => new PackageIdentity(identity.Id, version)));
                        }
                        catch (NuGetProtocolException)
                        {
                            // Ignore exception if packages folder does not exist
                            continue;
                        }
                    }
                }

                if (licensePackages.Count > 0 && !AcceptLicenseAgreement(licensePackages))
                {
                    token.ThrowIfCancellationRequested();
                    var pluralSuffix = licensePackages.Count == 1 ? "s" : "";
                    var message = $"Unable to install package '{package}' because '{string.Join(", ", licensePackages.Select(x => x.Identity))}' require{pluralSuffix} license acceptance.";
                    logger.LogError(message);
                    throw new InvalidOperationException(message);
                }

                // Get dependencies from removed packages while they are still installed
                if (packagesToRemove.Count > 0)
                {
                    localPackages = await GetDependencyInfoAsync(dependencyInfoResource, packagesToRemove, framework);
                    await DeletePackages(packagesToRemove, logger, token);
                }

                var targetPackage = default(PackageReaderBase);
                var packageExtractionContext = new PackageExtractionContext(
                    PackageSaveMode,
                    XmlDocFileSaveMode.None,
                    ClientPolicyContext.GetClientPolicy(Settings, logger),
                    NullLogger.Instance);
                foreach (var identity in installOperations)
                {
                    PackageReaderBase packageReader;
                    installedPath = PathResolver.GetInstalledPath(identity);
                    if (installedPath == null)
                    {
                        var packageInfo = sourcePackages[identity];
                        packageReader = await ExtractPackage(packageInfo, cacheContext, packageExtractionContext, logger, token);
                    }
                    else
                    {
                        packageReader = new PackageFolderReader(installedPath);
                    }

                    if (PackageIdentityComparer.Default.Equals(package, identity))
                    {
                        targetPackage = packageReader;
                    }
                }

                if (packagesToRemove.Count > 0)
                {
                    IDictionary<PackageIdentity, HashSet<PackageIdentity>> dependentPackages, packageDependencies;
                    installedPackages = (await GetInstalledPackagesAsync(token)).Select(info => info.Identity);
                    localPackages = localPackages.Union(await GetDependencyInfoAsync(dependencyInfoResource, installedPackages, framework));
                    GetPackageDependents(installedPackages, localPackages, out dependentPackages, out packageDependencies);
                    var uninstallOperations = GetPackagesToUninstall(packagesToRemove, packageDependencies, removeDependencies: true);
                    uninstallOperations = KeepActiveDependencies(uninstallOperations, packagesToRemove, dependentPackages, forceRemoveTargets: true, logger);
                    await DeletePackages(uninstallOperations, logger, token);
                }

                return targetPackage;
            }
        }

        static async Task GetPackageDependencies(
            string packageId,
            VersionRange versionRange,
            NuGetFramework framework,
            SourceCacheContext cacheContext,
            IEnumerable<SourceRepository> repositories,
            IDictionary<PackageIdentity, SourcePackageDependencyInfo> availablePackages,
            ILogger logger,
            bool ignoreDependencies,
            CancellationToken token)
        {
            var dependencyInfo = default(SourcePackageDependencyInfo);
            foreach (var sourceRepository in repositories)
            {
                var dependencyInfoResource = await sourceRepository.GetResourceAsync<DependencyInfoResource>(token);
                var dependencyPackages = await dependencyInfoResource.ResolvePackages(packageId, framework, cacheContext, NullLogger.Instance, token);
                foreach (var package in dependencyPackages)
                {
                    if (!versionRange.Satisfies(package.Version)) continue;
                    if (dependencyInfo == null || package.Version < dependencyInfo.Version)
                    {
                        dependencyInfo = package;
                    }
                }

                if (dependencyInfo != null)
                {
                    if (availablePackages.ContainsKey(dependencyInfo)) return;
                    availablePackages.Add(dependencyInfo, dependencyInfo);
                    if (!ignoreDependencies)
                    {
                        logger.LogInformation($"Attempting to resolve dependencies for '{dependencyInfo.Id} {dependencyInfo.Version}'.");
                        foreach (var dependency in dependencyInfo.Dependencies)
                        {
                            await GetPackageDependencies(
                                dependency.Id, dependency.VersionRange,
                                framework, cacheContext, repositories, availablePackages, logger, ignoreDependencies, token);
                        }
                    }
                    break;
                }
            }

            // dependency was not found in any repository
            if (dependencyInfo == null)
            {
                var message = $"The package '{packageId} {versionRange}' could not be found.";
                logger.LogError(message);
                throw new InvalidOperationException(message);
            }
        }

        public async Task<bool> UninstallPackageAsync(PackageIdentity package, bool removeDependencies, CancellationToken token)
        {
            var logger = Logger;
            IDictionary<PackageIdentity, HashSet<PackageIdentity>> dependentPackages, packageDependencies;
            var dependencyInfoResource = await LocalRepository.GetResourceAsync<DependencyInfoResource>(token);
            var installedPackages = (await GetInstalledPackagesAsync(token)).Select(info => info.Identity);
            var dependencyInfo = await GetDependencyInfoAsync(dependencyInfoResource, installedPackages, ProjectFramework);
            GetPackageDependents(installedPackages, dependencyInfo, out dependentPackages, out packageDependencies);
            var targetPackages = installedPackages.Where(p => PackageIdentity.Comparer.Equals(p, package));
            if (!targetPackages.Any())
            {
                logger.LogError($"The package '{package}' could not be found.");
                return false;
            }

            var packageOperations = GetPackagesToUninstall(targetPackages, packageDependencies, removeDependencies);
            packageOperations = KeepActiveDependencies(packageOperations, targetPackages, dependentPackages, forceRemoveTargets: false, logger);
            await DeletePackages(packageOperations, logger, token);
            return true;
        }

        static async Task<IEnumerable<SourcePackageDependencyInfo>> GetDependencyInfoAsync(
            DependencyInfoResource dependencyInfoResource,
            IEnumerable<PackageIdentity> packages,
            NuGetFramework framework)
        {
            try
            {
                var result = new HashSet<SourcePackageDependencyInfo>(PackageIdentity.Comparer);
                foreach (var package in packages)
                {
                    var dependencyInfo = await dependencyInfoResource.ResolvePackage(
                        package,
                        framework,
                        NullSourceCacheContext.Instance,
                        NullLogger.Instance,
                        CancellationToken.None);
                    if (dependencyInfo != null)
                    {
                        result.Add(dependencyInfo);
                    }
                }

                return result;
            }
            catch (NuGetProtocolException)
            {
                return null;
            }
        }

        static void GetPackageDependents(
            IEnumerable<PackageIdentity> installedPackages,
            IEnumerable<PackageDependencyInfo> dependencyInfo,
            out IDictionary<PackageIdentity, HashSet<PackageIdentity>> dependentPackages,
            out IDictionary<PackageIdentity, HashSet<PackageIdentity>> packageDependencies)
        {
            dependentPackages = new Dictionary<PackageIdentity, HashSet<PackageIdentity>>(PackageIdentity.Comparer);
            packageDependencies = new Dictionary<PackageIdentity, HashSet<PackageIdentity>>(PackageIdentity.Comparer);
            foreach (var info in dependencyInfo)
            {
                var package = new PackageIdentity(info.Id, info.Version);
                foreach (var dependency in info.Dependencies)
                {
                    var matchingDependency = installedPackages.FirstOrDefault(di =>
                        dependency.Id.Equals(di.Id, StringComparison.OrdinalIgnoreCase) &&
                        dependency.VersionRange.Satisfies(di.Version));
                    if (matchingDependency != null)
                    {
                        if (!dependentPackages.TryGetValue(matchingDependency, out HashSet<PackageIdentity> dependents))
                        {
                            dependents = new HashSet<PackageIdentity>(PackageIdentity.Comparer);
                            dependentPackages.Add(matchingDependency, dependents);
                        }
                        dependents.Add(package);

                        if (!packageDependencies.TryGetValue(package, out HashSet<PackageIdentity> dependencies))
                        {
                            dependencies = new HashSet<PackageIdentity>(PackageIdentity.Comparer);
                            packageDependencies.Add(package, dependencies);
                        }
                        dependencies.Add(matchingDependency);
                    }
                }
            }
        }

        static IEnumerable<PackageIdentity> GetPackagesToUninstall(
            IEnumerable<PackageIdentity> targetPackages,
            IDictionary<PackageIdentity, HashSet<PackageIdentity>> packageDependencies,
            bool removeDependencies)
        {
            var queue = new Queue<PackageIdentity>();
            var result = new List<PackageIdentity>();
            foreach (var package in targetPackages)
            {
                queue.Enqueue(package);
            }

            while (queue.Count > 0)
            {
                var next = queue.Dequeue();
                result.Add(next);

                if (removeDependencies && packageDependencies.TryGetValue(next, out HashSet<PackageIdentity> dependencies))
                {
                    foreach (var dependency in dependencies)
                    {
                        if (result.Remove(dependency))
                        {
                            result.Add(dependency);
                        }
                        else queue.Enqueue(dependency);
                    }
                }
            }

            return result;
        }

        static IEnumerable<PackageIdentity> KeepActiveDependencies(
            IEnumerable<PackageIdentity> packagesToRemove,
            IEnumerable<PackageIdentity> targetPackages,
            IDictionary<PackageIdentity, HashSet<PackageIdentity>> dependentPackages,
            bool forceRemoveTargets,
            ILogger logger)
        {
            var unusedDependencies = new List<PackageIdentity>(packagesToRemove);
            unusedDependencies.RemoveAll(package =>
            {
                if (targetPackages.Contains(package))
                {
                    if (forceRemoveTargets) return false;
                    if (dependentPackages.TryGetValue(package, out HashSet<PackageIdentity> dependents))
                    {
                        var pluralSuffix = dependents.Count == 1 ? "s" : "";
                        var message = $"Unable to uninstall '{package}' because '{string.Join(", ", dependents)}' depend{pluralSuffix} on it.";
                        logger.LogError(message);
                        throw new InvalidOperationException(message);
                    }
                }

                var transitiveDependents = new HashSet<PackageIdentity>();
                GetTransitiveDependents(package, transitiveDependents, dependentPackages);
                return !transitiveDependents.IsSubsetOf(packagesToRemove);
            });

            return unusedDependencies;
        }

        static void GetTransitiveDependents(
            PackageIdentity package,
            HashSet<PackageIdentity> transitiveDependents,
            IDictionary<PackageIdentity, HashSet<PackageIdentity>> dependentPackages)
        {
            if (dependentPackages.TryGetValue(package, out HashSet<PackageIdentity> dependents))
            {
                transitiveDependents.UnionWith(dependents);
                foreach (var dependent in dependents)
                {
                    GetTransitiveDependents(dependent, transitiveDependents, dependentPackages);
                }
            }
        }

        async Task DeletePackages(IEnumerable<PackageIdentity> packages, ILogger logger, CancellationToken token)
        {
            foreach (var package in packages)
            {
                var installPath = PathResolver.GetInstalledPath(package);
                var localPackage = LocalRepository.GetLocalPackage(package);
                if (localPackage != null)
                {
                    await DeletePackage(localPackage, installPath, logger, token);
                }
            }
        }
    }
}
