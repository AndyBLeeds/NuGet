﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using NuGet.Common;

namespace NuGet.Commands
{
    [Command(typeof(NuGetCommand), "restore", "RestoreCommandDescription",
        MinArgs = 0, MaxArgs = 1, UsageSummaryResourceName = "RestoreCommandUsageSummary",
        UsageDescriptionResourceName = "RestoreCommandUsageDescription",
        UsageExampleResourceName = "RestoreCommandUsageExamples")]
    public class RestoreCommand : Command
    {
        private readonly IPackageRepository _cacheRepository;
        private readonly List<string> _sources = new List<string>();
        
        // True means we're restoring for a solution; False means we're restoring packages
        // listed in a packages.config file.
        private bool _restoringForSolution;
                
        private string _solutionFileFullPath;
        private string _packagesConfigFileFullPath;

        // A flag indicating if the opt-out message should be displayed.
        private bool _outputOptOutMessage;

        // lock used to access _outputOptOutMessage.
        private readonly object _outputOptOutMessageLock = new object();

        [Option(typeof(NuGetCommand), "RestoreCommandSourceDescription")]
        public ICollection<string> Source
        {
            get { return _sources; }
        }

        [Option(typeof(NuGetCommand), "RestoreCommandNoCache")]
        public bool NoCache { get; set; }

        [Option(typeof(NuGetCommand), "RestoreCommandRequireConsent")]
        public bool RequireConsent { get; set; }

        [Option(typeof(NuGetCommand), "RestoreCommandDisableParallelProcessing")]
        public bool DisableParallelProcessing { get; set; }

        [Option(typeof(NuGetCommand), "RestoreCommandPackagesDirectory", AltName="OutputDirectory")]
        public string PackagesDirectory { get; set; }

        [Option(typeof(NuGetCommand), "RestoreCommandSolutionDirectory")]
        public string SolutionDirectory { get; set; }

        /// <remarks>
        /// Meant for unit testing.
        /// </remarks>
        protected IPackageRepository CacheRepository
        {
            get { return _cacheRepository; }
        }

        /// <remarks>
        /// Meant for unit testing.
        /// </remarks>
        internal bool RestoringForSolution
        {
            get { return _restoringForSolution; }
        }

        /// <remarks>
        /// Meant for unit testing.
        /// </remarks>
        internal string SolutionFileFullPath
        {
            get { return _solutionFileFullPath; }
        }

        /// <remarks>
        /// Meant for unit testing.
        /// </remarks>
        internal string PackagesConfigFileFullPath
        {
            get { return _packagesConfigFileFullPath; }
        }

        [ImportingConstructor]
        public RestoreCommand()
            : this(MachineCache.Default)
        {
        }

        protected internal RestoreCommand(IPackageRepository cacheRepository)
        {
            _cacheRepository = cacheRepository;
            _outputOptOutMessage = true;
        }

        internal void DetermineRestoreMode()
        {
            if (Arguments.Count == 0)
            {
                // look for solution files first
                var slnFiles = FileSystem.GetFiles("", "*.sln").ToArray();
                if (slnFiles.Length > 1)
                {
                    throw new InvalidOperationException(NuGetResources.Error_MultipleSolutions);
                }

                if (slnFiles.Length == 1)
                {
                    _restoringForSolution = true;
                    _solutionFileFullPath = FileSystem.GetFullPath(slnFiles[0]);
                    if (Verbosity == Verbosity.Detailed)
                    {
                        Console.WriteLine(NuGetResources.RestoreCommandRestoringPackagesForSolution, _solutionFileFullPath);
                    }

                    return;
                }

                // look for packages.config file
                if (FileSystem.FileExists(Constants.PackageReferenceFile))
                {
                    _restoringForSolution = false;
                    _packagesConfigFileFullPath = FileSystem.GetFullPath(Constants.PackageReferenceFile);
                    if (Verbosity == NuGet.Verbosity.Detailed)
                    {
                        Console.WriteLine(NuGetResources.RestoreCommandRestoringPackagesFromPackagesConfigFile);
                    }

                    return;
                }

                throw new InvalidOperationException(NuGetResources.Error_NoSolutionFileNorePackagesConfigFile);
            }
            else
            {
                if (Path.GetFileName(Arguments[0]).Equals(Constants.PackageReferenceFile, StringComparison.OrdinalIgnoreCase))
                {
                    // restoring from packages.config file
                    _restoringForSolution = false;
                    _packagesConfigFileFullPath = FileSystem.GetFullPath(Arguments[0]);
                }
                else
                {
                    _restoringForSolution = true;
                    _solutionFileFullPath = FileSystem.GetFullPath(Arguments[0]);
                }
            }
        }

        protected internal virtual IFileSystem CreateFileSystem(string path)
        {
            path = FileSystem.GetFullPath(path);
            return new PhysicalFileSystem(path);
        }

        private void ReadSettings()
        {
            if (_restoringForSolution || !String.IsNullOrEmpty(SolutionDirectory))
            {
                var solutionDirectory = _restoringForSolution ?
                    Path.GetDirectoryName(_solutionFileFullPath) :
                    SolutionDirectory;

                // Read the solution-level settings
                var solutionSettingsFile = Path.Combine(
                    solutionDirectory, 
                    NuGetConstants.NuGetSolutionSettingsFolder);
                var fileSystem = CreateFileSystem(solutionSettingsFile);

                Settings = NuGet.Settings.LoadDefaultSettings(
                    fileSystem: fileSystem,
                    configFileName: ConfigFile,
                    machineWideSettings: MachineWideSettings);

                // Recreate the source provider and credential provider
                SourceProvider = PackageSourceBuilder.CreateSourceProvider(Settings);
                HttpClient.DefaultCredentialProvider = new SettingsCredentialProvider(new ConsoleCredentialProvider(Console), SourceProvider, Console);
            }
        }

        private string GetPackagesFolder()
        {
            if (!String.IsNullOrEmpty(PackagesDirectory))
            {
                return PackagesDirectory;
            }

            var repositoryPath = Settings.GetRepositoryPath();
            if (!String.IsNullOrEmpty(repositoryPath))
            {
                return repositoryPath;
            }

            if (!String.IsNullOrEmpty(SolutionDirectory))
            {
                return Path.Combine(SolutionDirectory, CommandLineConstants.PackagesDirectoryName);
            }

            if (_restoringForSolution)
            {
                return Path.Combine(Path.GetDirectoryName(_solutionFileFullPath), CommandLineConstants.PackagesDirectoryName);
            }

            throw new InvalidOperationException(NuGetResources.RestoreCommandCannotDeterminePackagesFolder);
        }

        protected PackageReferenceFile GetPackageReferenceFile(string fullPath)
        {
            return new PackageReferenceFile(FileSystem, fullPath);
        }

        // Do a very quick check of whether a package in installed by checking whether the nupkg file exists
        private static bool IsPackageInstalled(IPackageRepository repository, IFileSystem packagesFolderFileSystem, string packageId, SemanticVersion version)
        {
            if (version != null)
            {
                // If we know exactly what package to lookup, check if it's already installed locally. 
                // We'll do this by checking if the package directory exists on disk.
                var localRepository = (LocalPackageRepository)repository;
                var packagePaths = localRepository.GetPackageLookupPaths(packageId, version);
                return packagePaths.Any(packagesFolderFileSystem.FileExists);
            }
            return false;
        }

        private IPackageRepository GetRepository()
        {
            var repository = AggregateRepositoryHelper.CreateAggregateRepositoryFromSources(RepositoryFactory, SourceProvider, Source);
            bool ignoreFailingRepositories = repository.IgnoreFailingRepositories;
            if (!NoCache)
            {
                repository = new AggregateRepository(new[] { CacheRepository, repository }) { IgnoreFailingRepositories = ignoreFailingRepositories };
            }
            repository.Logger = Console;
            return repository;
        }

        protected virtual IPackageManager CreatePackageManager(IFileSystem packagesFolderFileSystem)
        {
            var repository = GetRepository();
            var pathResolver = new DefaultPackagePathResolver(packagesFolderFileSystem, useSideBySidePaths: true);

            IPackageRepository localRepository = new LocalPackageRepository(pathResolver, packagesFolderFileSystem);
            var packageManager = new PackageManager(repository, pathResolver, packagesFolderFileSystem, localRepository)
            {
                Logger = Console
            };

            return packageManager;
        }

        private void EnsurePackageRestoreConsent(bool packageRestoreConsent)
        {
            if (RequireConsent && !packageRestoreConsent)
            {
                string message = string.Format(
                    CultureInfo.CurrentCulture,
                    LocalizedResourceManager.GetString("InstallCommandPackageRestoreConsentNotFound"),
                    NuGet.Resources.NuGetResources.PackageRestoreConsentCheckBoxText.Replace("&", ""));
                throw new InvalidOperationException(message);
            }
        }

        private bool RestorePackage(
            IFileSystem packagesFolderFileSystem,
            string packageId,
            SemanticVersion version,
            bool packageRestoreConsent,
            ConcurrentQueue<IPackage> satellitePackages)
        {
            var packageManager = CreatePackageManager(packagesFolderFileSystem);
            if (IsPackageInstalled(packageManager.LocalRepository, packagesFolderFileSystem, packageId, version))
            {
                return false;
            }

            EnsurePackageRestoreConsent(packageRestoreConsent);
            if (RequireConsent && _outputOptOutMessage)
            {
                lock (_outputOptOutMessageLock)
                {
                    if (_outputOptOutMessage)
                    {
                        string message = String.Format(
                            CultureInfo.CurrentCulture,
                            LocalizedResourceManager.GetString("RestoreCommandPackageRestoreOptOutMessage"),
                            NuGet.Resources.NuGetResources.PackageRestoreConsentCheckBoxText.Replace("&", ""));
                        Console.WriteLine(message);
                        _outputOptOutMessage = false;
                    }
                }
            }

            using (packageManager.SourceRepository.StartOperation(RepositoryOperationNames.Restore, packageId))
            {
                var package = PackageHelper.ResolvePackage(packageManager.SourceRepository, packageId, version);
                if (package.IsSatellitePackage())
                {
                    // Satellite packages would necessarily have to be installed later than the corresponding package. 
                    // We'll collect them in a list to keep track and then install them later.
                    satellitePackages.Enqueue(package);
                    return true;
                }

                // During package restore with parallel build, multiple projects would try to write to disk simultaneously which results in write contentions.
                // We work around this issue by ensuring only one instance of the exe installs the package.
                PackageExtractor.InstallPackage(packageManager, package);
                return true;
            }
        }

        /// <returns>True if one or more packages are installed.</returns>
        private bool ExecuteInParallel(IFileSystem fileSystem, ICollection<PackageReference> packageReferences)
        {
            bool packageRestoreConsent = new PackageRestoreConsent(Settings).IsGranted;
            int defaultConnectionLimit = ServicePointManager.DefaultConnectionLimit;
            if (packageReferences.Count > defaultConnectionLimit)
            {
                ServicePointManager.DefaultConnectionLimit = Math.Min(10, packageReferences.Count);
            }

            // The PackageSourceProvider reads from the underlying ISettings multiple times. One of the fields it reads is the password which is consequently decrypted
            // once for each package being installed. Per work item 2345, a couple of users are running into an issue where this results in an exception in native 
            // code. Instead, we'll use a cached set of sources. This should solve the issue and also give us some perf boost.
            SourceProvider = new CachedPackageSourceProvider(SourceProvider);

            var satellitePackages = new ConcurrentQueue<IPackage>();

            if (DisableParallelProcessing)
            {
                foreach (var package in packageReferences)
                {
                    RestorePackage(fileSystem, package.Id, package.Version, packageRestoreConsent, satellitePackages);
                }

                return true;
            }

            var tasks = packageReferences.Select(package =>
                            Task.Factory.StartNew(() => RestorePackage(fileSystem, package.Id, package.Version, packageRestoreConsent, satellitePackages))).ToArray();

            Task.WaitAll(tasks);
            // Return true if we installed any satellite packages or if any of our install tasks succeeded.
            return InstallSatellitePackages(fileSystem, satellitePackages) ||
                   tasks.All(p => !p.IsFaulted && p.Result);
        }

        private bool InstallSatellitePackages(IFileSystem packagesFolderFileSystem, ConcurrentQueue<IPackage> satellitePackages)
        {
            if (satellitePackages.Count == 0)
            {
                return false;
            }

            var packageManager = CreatePackageManager(packagesFolderFileSystem);
            foreach (var package in satellitePackages)
            {
                packageManager.InstallPackage(package, ignoreDependencies: true, allowPrereleaseVersions: false);
            }
            return true;
        }

        private void InstallPackagesFromConfigFile(IFileSystem packagesFolderFileSystem, string fileName)
        {
            PackageReferenceFile file = GetPackageReferenceFile(fileName);
            var packageReferences = CommandLineUtility.GetPackageReferences(file, fileName, requireVersion: true);

            bool installedAny = ExecuteInParallel(packagesFolderFileSystem, packageReferences);
            if (!installedAny && packageReferences.Any())
            {
                Console.WriteLine(NuGetResources.InstallCommandNothingToInstall, Constants.PackageReferenceFile);
            }
        }

        private void RestorePackagesFromConfigFile(string packageRerenceFileName, IFileSystem packagesFolderFileSystem)
        {
            if (FileSystem.FileExists(packageRerenceFileName))
            {
                if (Console.Verbosity == NuGet.Verbosity.Detailed)
                {
                    Console.WriteLine(NuGetResources.RestoreCommandRestoringPackagesListedInFile, packageRerenceFileName);
                }

                InstallPackagesFromConfigFile(packagesFolderFileSystem, packageRerenceFileName);
            }
        }

        private void RestorePackagesForSolution(
            IFileSystem packagesFolderFileSystem, string solutionFileFullPath)
        {
            var solution = new Solution(solutionFileFullPath);
            var solutionDirectory = Path.GetDirectoryName(solutionFileFullPath);

            // restore packages for the solution
            var solutionSettingsFolder = Path.Combine(solutionDirectory, NuGetConstants.NuGetSolutionSettingsFolder);
            var packageRerenceFileName = Path.Combine(solutionSettingsFolder, Constants.PackageReferenceFile);
            RestorePackagesFromConfigFile(packageRerenceFileName, packagesFolderFileSystem);

            // restore packages for projects
            foreach (var project in solution.Projects)
            {
                if (!project.IsMSBuildProject)
                {
                    continue;
                }

                var projectFile = Path.Combine(solutionDirectory, project.RelativePath);
                if (!FileSystem.FileExists(projectFile))
                {
                    Console.WriteWarning(NuGetResources.RestoreCommandProjectNotFound, projectFile);
                    continue;
                }

                if (IsPackageRestoreNeeded(projectFile))
                {
                    packageRerenceFileName = Path.Combine(
                        Path.GetDirectoryName(projectFile),
                        Constants.PackageReferenceFile);
                    RestorePackagesFromConfigFile(packageRerenceFileName, packagesFolderFileSystem);
                }
            }
        }

        /// <summary>
        /// Indicates if package restore is needed for the project.
        /// </summary>
        /// <param name="projectFile">The project file.</param>
        /// <returns>True if package restore is needed.</returns>
        private static bool IsPackageRestoreNeeded(string projectFile)
        {
            try
            {
                MSBuildProjectSystem proj = new MSBuildProjectSystem(projectFile);
                return proj.FileExistsInProject(Constants.PackageReferenceFile);
            }
            catch (Microsoft.Build.Exceptions.InvalidProjectFileException)
            {
                // If the project cannot be loaded, we assume this is because of
                // missing package.
                return true;
            }
        }

        public override void ExecuteCommand()
        {
            DetermineRestoreMode();
            if (_restoringForSolution && !String.IsNullOrEmpty(SolutionDirectory))
            {
                // option -SolutionDirectory is not valid when we are restoring packages for a solution
                throw new InvalidOperationException(NuGetResources.RestoreCommandOptionSolutionDirectoryIsInvalid);
            }

            ReadSettings();
            string packagesFolder = GetPackagesFolder();
            IFileSystem packagesFolderFileSystem = CreateFileSystem(packagesFolder);

            if (!_restoringForSolution)
            {
                // By default the PackageReferenceFile does not throw if the file does not exist at the specified path.
                // So we'll need to verify that the file exists.
                if (!FileSystem.FileExists(_packagesConfigFileFullPath))
                {
                    string message = String.Format(CultureInfo.CurrentCulture, NuGetResources.RestoreCommandFileNotFound, _packagesConfigFileFullPath);
                    throw new InvalidOperationException(message);
                }

                InstallPackagesFromConfigFile(packagesFolderFileSystem, _packagesConfigFileFullPath);
            }
            else
            {
                RestorePackagesForSolution(packagesFolderFileSystem, _solutionFileFullPath);
            }
        }
    }
}
