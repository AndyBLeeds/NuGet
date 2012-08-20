﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;

namespace NuGet.Commands
{
    [Command(typeof(NuGetCommand), "install", "InstallCommandDescription",
        MinArgs = 0, MaxArgs = 1, UsageSummaryResourceName = "InstallCommandUsageSummary",
        UsageDescriptionResourceName = "InstallCommandUsageDescription",
        UsageExampleResourceName = "InstallCommandUsageExamples")]
    public class InstallCommand : Command
    {
        private readonly IPackageRepository _cacheRepository;
        private readonly List<string> _sources = new List<string>();
        private readonly ISettings _configSettings;

        [Option(typeof(NuGetCommand), "InstallCommandSourceDescription")]
        public ICollection<string> Source
        {
            get { return _sources; }
        }

        [Option(typeof(NuGetCommand), "InstallCommandOutputDirDescription")]
        public string OutputDirectory { get; set; }

        [Option(typeof(NuGetCommand), "InstallCommandVersionDescription")]
        public string Version { get; set; }

        [Option(typeof(NuGetCommand), "InstallCommandExcludeVersionDescription", AltName = "x")]
        public bool ExcludeVersion { get; set; }

        [Option(typeof(NuGetCommand), "InstallCommandPrerelease")]
        public bool Prerelease { get; set; }

        [Option(typeof(NuGetCommand), "InstallCommandNoCache")]
        public bool NoCache { get; set; }
        internal string InstallPath
        {
            get
            {
                // Use the passed-in install path if any;
                // if none specified, look in the default config file;
                // if none specified, default to the current dir.
                string installPath = OutputDirectory;
                if (String.IsNullOrEmpty(installPath))
                {
                    installPath = _configSettings.GetRepositoryPath();
                    if (String.IsNullOrEmpty(installPath))
                    {
                        installPath = Directory.GetCurrentDirectory();
                    }
                }
                return installPath;
            }
        }

        private IPackageRepositoryFactory RepositoryFactory { get; set; }

        private IPackageSourceProvider SourceProvider { get; set; }

        /// <remarks>
        /// Meant for unit testing.
        /// </remarks>
        protected IPackageRepository CacheRepository
        {
            get { return _cacheRepository; }
        }

        private bool AllowMultipleVersions
        {
            get { return !ExcludeVersion; }
        }

        [ImportingConstructor]
        public InstallCommand(IPackageRepositoryFactory packageRepositoryFactory, IPackageSourceProvider sourceProvider, IFileSystem startingPoint)
            : this(packageRepositoryFactory, sourceProvider, Settings.LoadDefaultSettings(startingPoint), MachineCache.Default)
        {
        }

        protected internal InstallCommand(
            IPackageRepositoryFactory packageRepositoryFactory,
            IPackageSourceProvider sourceProvider,
            ISettings configSettings,
            IPackageRepository cacheRepository)
        {
            if (packageRepositoryFactory == null)
            {
                throw new ArgumentNullException("packageRepositoryFactory");
            }

            if (sourceProvider == null)
            {
                throw new ArgumentNullException("sourceProvider");
            }

            if (configSettings == null)
            {
                throw new ArgumentNullException("configSettings");
            }

            RepositoryFactory = packageRepositoryFactory;
            SourceProvider = sourceProvider;
            _cacheRepository = cacheRepository;
            _configSettings = configSettings;
        }

        public override void ExecuteCommand()
        {
            IFileSystem fileSystem = CreateFileSystem();

            // If the first argument is a packages.config file, install everything it lists
            // Otherwise, treat the first argument as a package Id
            if (Arguments.Count == 0 || Path.GetFileName(Arguments[0]).Equals(Constants.PackageReferenceFile, StringComparison.OrdinalIgnoreCase))
            {
                Prerelease = true;
                var configFilePath = Path.GetFullPath(Arguments.Count == 0 ? Constants.PackageReferenceFile : Arguments[0]);
                // By default the PackageReferenceFile does not throw if the file does not exist at the specified path.
                // We'll try reading from the file so that the file system throws a file not found
                EnsureFileExists(fileSystem, configFilePath);
                InstallPackagesFromConfigFile(fileSystem, GetPackageReferenceFile(configFilePath), configFilePath);
            }
            else
            {
                string packageId = Arguments[0];
                SemanticVersion version = Version != null ? new SemanticVersion(Version) : null;

                bool result = InstallPackage(fileSystem, packageId, version, ignoreDependencies: false, packageRestoreConsent: true, operation: RepositoryOperationNames.Install);
                if (!result)
                {
                    Console.WriteLine(NuGetResources.InstallCommandPackageAlreadyExists, packageId);
                }
            }
        }

        protected virtual PackageReferenceFile GetPackageReferenceFile(string path)
        {
            return new PackageReferenceFile(Path.GetFullPath(path));
        }

        private IPackageRepository GetRepository()
        {
            var repository = AggregateRepositoryHelper.CreateAggregateRepositoryFromSources(RepositoryFactory, SourceProvider, Source, useBlobStorageSourceForDefault: true);
            
            bool ignoreFailingRepositories = repository.IgnoreFailingRepositories;
            if (!NoCache)
            {
                repository = new AggregateRepository(new[] { CacheRepository, repository }) 
                { 
                    IgnoreFailingRepositories = ignoreFailingRepositories 
                };
            }
            repository.Logger = Console;
            return repository;
        }

        private void InstallPackagesFromConfigFile(IFileSystem fileSystem, PackageReferenceFile file, string fileName)
        {
            var packageReferences = CommandLineUtility.GetPackageReferences(file, fileName, requireVersion: true);

            bool installedAny = ExecuteInParallel(fileSystem, packageReferences);
            if (!installedAny && packageReferences.Any())
            {
                Console.WriteLine(NuGetResources.InstallCommandNothingToInstall, Constants.PackageReferenceFile);
            }
        }

        private bool ExecuteInParallel(IFileSystem fileSystem, ICollection<PackageReference> packageReferences)
        {
            bool packageRestore = new PackageRestoreConsent(_configSettings).IsGranted;
            int defaultConnectionLimit = ServicePointManager.DefaultConnectionLimit;
            if (packageReferences.Count > defaultConnectionLimit)
            {
                ServicePointManager.DefaultConnectionLimit = Math.Min(10, packageReferences.Count);
            }

            var tasks = packageReferences.Select(package =>
                            Task.Factory.StartNew(() => InstallPackage(fileSystem, package.Id, package.Version, ignoreDependencies: true, packageRestoreConsent: packageRestore, operation: RepositoryOperationNames.Restore))).ToArray();

            Task.WaitAll(tasks);
            return tasks.All(p => !p.IsFaulted && p.Result);
        }

        private bool InstallPackage(
            IFileSystem fileSystem,
            string packageId,
            SemanticVersion version,
            bool ignoreDependencies,
            bool packageRestoreConsent,
            string operation)
        {
            var packageManager = CreatePackageManager(fileSystem);
            using (packageManager.SourceRepository.StartOperation(operation))
            {
                if (ExcludeVersion || (version != null))
                {
                    // If we know exactly what package to lookup or we are not installing packages side by side, check if it's already installed locally. 
                    // We'll do this by checking if the package directory exists on disk.
                    var localRepository = packageManager.LocalRepository as LocalPackageRepository;
                    Debug.Assert(localRepository != null, "The PackageManager's local repository instance is necessarily a LocalPackageRepository instance.");
                    if (IsPackageInstalled(localRepository, packageManager.FileSystem, packageId, version))
                    {
                        return false;
                    }
                }

                EnsurePackageRestoreConsent(packageRestoreConsent);

                // During package restore with parallel build, multiple projects would try to write to disk simultaneously which results in write contentions.
                // We work around this issue by ensuring only one instance of the exe installs the package.
                var uniqueToken = GenerateUniqueToken(packageManager, packageId, version);
                ExecuteLocked(uniqueToken, () => packageManager.InstallPackage(packageId, version, ignoreDependencies: ignoreDependencies, allowPrereleaseVersions: Prerelease));
                return true;
            }
        }

        protected virtual IPackageManager CreatePackageManager(IFileSystem fileSystem)
        {
            var repository = GetRepository();
            var pathResolver = new DefaultPackagePathResolver(fileSystem, useSideBySidePaths: AllowMultipleVersions);

            IPackageRepository localRepository = new LocalPackageRepository(pathResolver, fileSystem);
            var packageManager = new PackageManager(repository, pathResolver, fileSystem, localRepository)
                                 {
                                     Logger = Console
                                 };

            return packageManager;
        }

        protected virtual IFileSystem CreateFileSystem()
        {
            return new PhysicalFileSystem(InstallPath);
        }

        private static void EnsureFileExists(IFileSystem fileSystem, string configFilePath)
        {
            using (fileSystem.OpenFile(configFilePath))
            {
                // Do nothing
            }
        }

        private static void EnsurePackageRestoreConsent(bool packageRestoreConsent)
        {
            if (!packageRestoreConsent)
            {
                throw new InvalidOperationException(LocalizedResourceManager.GetString("InstallCommandPackageRestoreConsentNotFound"));
            }
        }

        // Do a very quick check of whether a package in installed by checking whether the nupkg file exists
        private static bool IsPackageInstalled(LocalPackageRepository packageRepository, IFileSystem fileSystem, string packageId, SemanticVersion version)
        {
            var packagePaths = packageRepository.GetPackageLookupPaths(packageId, version);
            return packagePaths.Any(fileSystem.FileExists);
        }

        /// <summary>
        /// We want to base the lock name off of the full path of the package, however, the Mutex looks for files on disk if a path is given.
        /// Additionally, it also fails if the string is longer than 256 characters. Therefore we obtain a base-64 encoded hash of the path.
        /// </summary>
        /// <seealso cref="http://social.msdn.microsoft.com/forums/en-us/clr/thread/D0B3BF82-4D23-47C8-8706-CC847157AC81"/>
        private static string GenerateUniqueToken(IPackageManager packageManager, string packageId, SemanticVersion version)
        {
            var packagePath = packageManager.FileSystem.GetFullPath(packageManager.PathResolver.GetPackageFileName(packageId, version));
            var pathBytes = Encoding.UTF8.GetBytes(packagePath);
            var hashProvider = new CryptoHashProvider("SHA256");

            return Convert.ToBase64String(hashProvider.CalculateHash(pathBytes)).ToUpperInvariant();
        }

        private static void ExecuteLocked(string name, Action action)
        {
            bool created;
            using (var mutex = new Mutex(initiallyOwned: true, name: name, createdNew: out created))
            {
                try
                {
                    // We need to ensure only one instance of the executable performs the install. All other instances need to wait 
                    // for the package to be installed. We'd cap the waiting duration so that other instances aren't waiting indefinitely.
                    if (created)
                    {
                        action();
                    }
                    else
                    {
                        mutex.WaitOne(TimeSpan.FromMinutes(2));
                    }
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }
    }
}