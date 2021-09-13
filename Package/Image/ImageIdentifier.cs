using OpenTap.Package;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Tap.Shared;

namespace OpenTap.Package
{
    /// <summary>
    /// Image that specifies a list of <see cref="PackageSpecifier"/> to install and a list of repositories to get the packages from.
    /// </summary>
    public class ImageIdentifier
    {
        internal bool Cached => cacheFileLookup.Count == Packages.Count();

        /// <summary>
        /// Image ID created by hashing the Packages list
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// Package configuration of the Image
        /// </summary>
        public ReadOnlyCollection<PackageDef> Packages { get; }

        /// <summary>
        /// Repositories to retrieve the packages from
        /// </summary>
        public ReadOnlyCollection<string> Repositories { get; }

        internal Dictionary<PackageDef, string> cacheFileLookup = new Dictionary<PackageDef, string>();

        /// <summary>
        /// An ImageIdentifier is immutable, but can be converted to an <see cref="ImageSpecifier"/> which can be manipulated.
        /// </summary>
        /// <returns><see cref="ImageSpecifier"/></returns>
        public ImageSpecifier ToSpecifier()
        {
            return new ImageSpecifier()
            {
                Packages = Packages.Select(s => new PackageSpecifier(s)).ToList(),
                Repositories = Repositories.ToList()
            };
        }

        internal ImageIdentifier(IEnumerable<PackageDef> packages, IEnumerable<string> repositories)
        {
            if (packages is null)
            {
                throw new ArgumentNullException(nameof(packages));
            }

            if (repositories is null)
            {
                throw new ArgumentNullException(nameof(repositories));
            }

            var packageList = packages.OrderBy(s => s.Name).ToList();
            Id = CalculateId(packageList);
            Repositories = new ReadOnlyCollection<string>(repositories.ToArray());
            Packages = new ReadOnlyCollection<PackageDef>(packageList);
        }

        private string CalculateId(IEnumerable<PackageDef> packageList)
        {
            List<string> packageHashes = new List<string>();
            foreach (PackageDef pkg in packageList)
            {
                if (pkg.Hash != null)
                    packageHashes.Add(pkg.Hash);
                else
                {
                    // This can happen if the package was created with OpenTAP < 9.16 that did not set the Hash property.
                    // We can just try to compute the hash now.
                    try
                    {
                        packageHashes.Add(pkg.ComputeHash());
                    }
                    catch
                    {
                        // This might happen if the PackageDef does not contain <Hash> elements for each file (for packages crated with OpenTAP < 9.5).
                        // In this case, just use the fields from IPackageIdentifier, they should be unique in most cases.
                        packageHashes.Add($"{pkg.Name} {pkg.Version} {pkg.Architecture} {pkg.OS}");
                    }
                }

            }


            HashAlgorithm algorithm = SHA1.Create();
            var bytes = algorithm.ComputeHash(Encoding.UTF8.GetBytes(string.Join(",", packageHashes)));
            return Convert.ToBase64String(bytes);
        }

        /// <summary>
        /// Deploy the <see cref="ImageIdentifier"/> as a OpenTAP installation.
        /// </summary>
        /// <param name="targetDir">Directory to deploy OpenTap installation. 
        /// If the directory is already an OpenTAP installation, the installation will be modified to match the image
        /// System-Wide packages are not removed
        /// </param>
        /// <param name="cancellationToken">Cancellation token</param>
        public void Deploy(string targetDir, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("Deployment operation cancelled by user");

            Installation currentInstallation = new Installation(targetDir);
            var packagesToUninstall = currentInstallation.GetPackages().Where(s => s.Class.ToLower() != "system-wide" && !Packages.Any(p => p.Name == s.Name));
            var modifyOrAdd = Packages.Where(s => !currentInstallation.GetPackages().Any(p => p.Name == s.Name && p.Version.ToString() == s.Version.ToString())).ToList();

            if (!packagesToUninstall.Any() && !modifyOrAdd.Any())
            {
                log.Info($"Target installation already matches specified image");
                return;
            }

            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("Deployment operation cancelled by user");

            if (packagesToUninstall.Any())
                Uninstall(packagesToUninstall, targetDir, cancellationToken);


            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("Deployment operation cancelled by user");

            if (modifyOrAdd.Any())
                Install(modifyOrAdd, targetDir, cancellationToken);
        }

        private void Install(IEnumerable<PackageDef> modifyOrAdd, string target, CancellationToken cancellationToken)
        {
            Installer installer = new Installer(target, cancellationToken) { DoSleep = false };
            var packagesInOrder = OrderPackagesForInstallation(modifyOrAdd);
            List<string> paths = new List<string>();
            foreach (var package in packagesInOrder)
            {
                if (!cacheFileLookup.ContainsKey(package))
                    Download(package);
                paths.Add(cacheFileLookup[package]);
            }
            installer.PackagePaths.Clear();
            installer.PackagePaths.AddRange(paths);


            List<Exception> installErrors = new List<Exception>();
            installer.Error += ex => installErrors.Add(ex);

            try
            {
                installer.InstallThread();
            }
            catch (Exception ex)
            {
                installErrors.Add(ex);
            }

            if (installErrors.Any())
                throw new AggregateException("Image deployment failed due to failiure in installing packages", installErrors);
        }

        private void Uninstall(IEnumerable<PackageDef> packagesToUninstall, string target, CancellationToken cancellationToken)
        {
            var orderedPackagesToUninstall = OrderPackagesForInstallation(packagesToUninstall);
            orderedPackagesToUninstall.Reverse();
            log.Info($"Removing packages:");
            foreach (var package in orderedPackagesToUninstall)
                log.Info($"- {package.Name} version {package.Version} ({package.Architecture}-{package.OS})");

            List<Exception> uninstallErrors = new List<Exception>();
            var newInstaller = new Installer(target, cancellationToken) { DoSleep = false };

            newInstaller.Error += ex => uninstallErrors.Add(ex);
            newInstaller.DoSleep = false;

            newInstaller.PackagePaths.AddRange(orderedPackagesToUninstall.Select(x => (x.PackageSource as InstalledPackageDefSource)?.PackageDefFilePath).ToList());
            int exitCode = newInstaller.RunCommand("uninstall", false, true);

            if (uninstallErrors.Any() || exitCode != 0)
                throw new AggregateException("Image deployment failed due to failiure in uninstalling existing packages", uninstallErrors);
        }

        private static List<PackageDef> OrderPackagesForInstallation(IEnumerable<PackageDef> packages)
        {
            var toInstall = new List<PackageDef>();

            var toBeSorted = packages.ToList();

            while (toBeSorted.Count() > 0)
            {
                var packagesWithNoRemainingDepsInList = toBeSorted.Where(pkg => pkg.Dependencies.All(dep => !toBeSorted.Any(p => p.Name == dep.Name))).ToList();
                toInstall.AddRange(packagesWithNoRemainingDepsInList);
                toBeSorted.RemoveAll(p => packagesWithNoRemainingDepsInList.Contains(p));
            }

            return toInstall;
        }

        /// <summary>
        /// Download all packages to the PackageCache. This is an optional step that can speed up deploying later.
        /// </summary>
        public void Cache()
        {
            if (Cached)
                return;
            foreach (var package in Packages)
                Download(package);
        }

        static TraceSource log = Log.CreateSource("Download");
        private void Download(PackageDef package)
        {
            string filename = PackageCacheHelper.GetCacheFilePath(package);

            if (File.Exists(filename))
            {
                log.Info($"Package {package.Name} exists in cache: {filename}");
                cacheFileLookup.Add(package, filename);
                return;
            }

            if (package.PackageSource is IFilePackageDefSource fileSource)
            {
                if (string.Equals(Path.GetPathRoot(fileSource.PackageFilePath), Path.GetPathRoot(PackageCacheHelper.PackageCacheDirectory), StringComparison.InvariantCultureIgnoreCase))
                    filename = fileSource.PackageFilePath;
            }
            else if (package.PackageSource is IRepositoryPackageDefSource repoSource)
            {
                IPackageRepository rm = PackageRepositoryHelpers.DetermineRepositoryType(repoSource.RepositoryUrl);
                log.Info($"Downloading {package.Name} version {package.Version} from {rm.Url}");
                rm.DownloadPackage(package, filename, CancellationToken.None);
            }
            cacheFileLookup.Add(package, filename);
        }
    }
}