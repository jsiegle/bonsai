﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Reflection;
using Bonsai.Design;
using Bonsai.Expressions;
using NuGet;
using System.Threading.Tasks;
using System.Windows.Forms;
using Bonsai.NuGet.Properties;

namespace Bonsai.NuGet
{
    public static class PackageHelper
    {
        public static void InstallExecutablePackage(IPackage package, IFileSystem fileSystem)
        {
            foreach (var file in package.GetContentFiles())
            {
                using (var stream = file.GetStream())
                {
                    fileSystem.AddFile(file.EffectivePath, stream);
                }
            }

            var manifest = Manifest.Create(package);
            var metadata = Manifest.Create(manifest.Metadata);
            var metadataPath = package.Id + global::NuGet.Constants.ManifestExtension;
            using (var stream = fileSystem.CreateFile(metadataPath))
            {
                metadata.Save(stream);
            }
        }

        public static void RunPackageOperation(LicenseAwarePackageManager packageManager, Func<Task> operationFactory, string operationLabel = null)
        {
            EventHandler<RequiringLicenseAcceptanceEventArgs> requiringLicenseHandler = null;
            using (var dialog = new PackageOperationDialog { ShowInTaskbar = true })
            {
                if (!string.IsNullOrEmpty(operationLabel)) dialog.Text = operationLabel;
                requiringLicenseHandler = (sender, e) =>
                {
                    if (dialog.InvokeRequired) dialog.Invoke(requiringLicenseHandler, sender, e);
                    else
                    {
                        dialog.Hide();
                        using (var licenseDialog = new LicenseAcceptanceDialog(e.LicensePackages))
                        {
                            e.LicenseAccepted = licenseDialog.ShowDialog() == DialogResult.Yes;
                            if (e.LicenseAccepted)
                            {
                                dialog.Show();
                            }
                        }
                    }
                };

                dialog.RegisterEventLogger((EventLogger)packageManager.Logger);
                var operation = operationFactory();
                operation.ContinueWith(task =>
                {
                    if (!task.IsFaulted)
                    {
                        dialog.BeginInvoke((Action)dialog.Close);
                    }
                });

                packageManager.RequiringLicenseAcceptance += requiringLicenseHandler;
                try { dialog.ShowDialog(); }
                finally { packageManager.RequiringLicenseAcceptance -= requiringLicenseHandler; }
            }
        }

        public static Task<IPackage> StartInstallPackage(this IPackageManager packageManager, string packageId, SemanticVersion version)
        {
            return Task.Factory.StartNew(() =>
            {
                try
                {
                    var logMessage = version == null ? Resources.InstallPackageLatestVersion : Resources.InstallPackageVersion;
                    packageManager.Logger.Log(MessageLevel.Info, logMessage, packageId, version);
                    var package = packageManager.SourceRepository.FindPackage(packageId, version);
                    if (package == null)
                    {
                        var errorMessage = version == null ? Resources.MissingPackageLatestVersion : Resources.MissingPackageVersion;
                        throw new InvalidOperationException(string.Format(errorMessage, packageId, version));
                    }
                    packageManager.InstallPackage(package, false, true);
                    return package;
                }
                catch (Exception ex)
                {
                    packageManager.Logger.Log(MessageLevel.Error, ex.Message);
                    throw;
                }
            });
        }

        public static Task<IPackage> StartUpdatePackage(this IPackageManager packageManager, string packageId, SemanticVersion version)
        {
            return Task.Factory.StartNew(() =>
            {
                try
                {
                    packageManager.Logger.Log(MessageLevel.Info, Resources.UpdatePackageLatestVersion, packageId);
                    var package = packageManager.SourceRepository.FindPackage(packageId, version);
                    if (package == null)
                    {
                        var errorMessage = string.Format(Resources.MissingPackageLatestVersion, packageId);
                        throw new InvalidOperationException(errorMessage);
                    }
                    packageManager.UpdatePackage(package, true, true);
                    return package;
                }
                catch (Exception ex)
                {
                    packageManager.Logger.Log(MessageLevel.Error, ex.Message);
                    throw;
                }
            });
        }

        public static Task<IPackage> StartRestorePackage(this IPackageManager packageManager, string id, SemanticVersion version)
        {
            return Task.Factory.StartNew(() =>
            {
                try
                {
                    packageManager.Logger.Log(MessageLevel.Info, Resources.RestorePackageVersion, id, version);
                    var package = packageManager.SourceRepository.FindPackage(id, version);
                    if (package == null)
                    {
                        var errorMessage = string.Format(Resources.MissingPackageVersion, id, version);
                        throw new InvalidOperationException(errorMessage);
                    }
                    return package;
                }
                catch (Exception ex)
                {
                    packageManager.Logger.Log(MessageLevel.Error, ex.Message);
                    throw;
                }
            });
        }
    }
}