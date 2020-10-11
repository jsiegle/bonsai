﻿using Bonsai.NuGet.Properties;
using NuGet;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reactive.Linq;
using System.Windows.Forms;

namespace Bonsai.NuGet
{
    public partial class GalleryDialog : Form
    {
        PackageManagerProxy packageManagerProxy;
        PackageViewController packageViewController;

        string targetPath;
        IPackage targetPackage;

        public GalleryDialog(string path)
        {
            InitializeComponent();
            packageManagerProxy = new PackageManagerProxy();
            packageManagerProxy.PackageInstalling += packageManagerProxy_PackageInstalling;
            packageViewController = new PackageViewController(
                path,
                this,
                packageView,
                packageDetails,
                packagePageSelector,
                packageManagerProxy,
                packageIcons,
                searchComboBox,
                prereleaseCheckBox,
                () => false,
                value => { },
                new[] { Constants.BonsaiDirectory, Constants.GalleryDirectory });
            InitializePackageSourceItems();
        }

        public string InstallPath { get; set; }

        public IPackageManager PackageManager
        {
            get { return packageManagerProxy; }
        }

        protected override void OnLoad(EventArgs e)
        {
            packageViewController.OnLoad(e);
            UpdateSelectedRepository();
            searchComboBox.Select();
            base.OnLoad(e);
        }

        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        {
            const int MaxImageSize = 256;
            packageView.ItemHeight = (int)(64 * factor.Height);
            packageIcons.ImageSize = new Size(
                Math.Min(MaxImageSize, (int)(32 * factor.Height)),
                Math.Min(MaxImageSize, (int)(32 * factor.Height)));
            base.ScaleControl(factor, specified);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            packageViewController.OnHandleDestroyed(e);
            base.OnHandleDestroyed(e);
        }

        protected override void OnResizeBegin(EventArgs e)
        {
            packageViewController.OnResizeBegin(e);
            base.OnResizeBegin(e);
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            packageViewController.OnResizeEnd(e);
            base.OnResizeEnd(e);
        }

        private void packageView_OperationClick(object sender, TreeViewEventArgs e)
        {
            var package = (IPackage)e.Node.Tag;
            if (package != null)
            {
                saveFolderDialog.FileName = package.Id;
                if (saveFolderDialog.ShowDialog(this) == DialogResult.OK)
                {
                    targetPackage = package;
                    targetPath = saveFolderDialog.FileName;
                    packageViewController.RunPackageOperation(new[] { package }, true);
                    if (DialogResult == DialogResult.OK)
                    {
                        Close();
                    }
                }
            }
        }

        void packageManagerProxy_PackageInstalling(object sender, PackageOperationEventArgs e)
        {
            var package = e.Package;
            if (package == targetPackage)
            {
                var entryPoint = package.Id + Constants.BonsaiExtension;
                if (!package.GetContentFiles().Any(file => file.EffectivePath == entryPoint))
                {
                    var message = string.Format(Resources.MissingWorkflowEntryPoint, entryPoint);
                    throw new InvalidOperationException(message);
                }

                var targetFileSystem = new PhysicalFileSystem(targetPath);
                InstallPath = PackageHelper.InstallExecutablePackage(package, targetFileSystem);
                DialogResult = DialogResult.OK;
            }
        }

        private void InitializePackageSourceItems()
        {
            packageSourceComboBox.Items.Clear();
            foreach (var pair in packageViewController.PackageManagers)
            {
                packageSourceComboBox.Items.Add(pair);
            }
        }

        private void UpdateSelectedRepository()
        {
            if (packageManagerProxy.SourceRepository == null) return;
            packageViewController.SetPackageViewStatus(Resources.NoItemsFoundLabel);
            packageViewController.ClearActiveRequests();

            var selectedItem = packageSourceComboBox.SelectedItem;
            if (selectedItem != null)
            {
                var selectedManager = ((KeyValuePair<string, PackageManager>)selectedItem).Value;
                packageViewController.SelectedRepository = selectedManager.SourceRepository;
            }
            else packageViewController.SelectedRepository = packageViewController.PackageManagers[Resources.AllNodeName].SourceRepository;

            packageView.OperationText = Resources.OpenOperationName;
            searchComboBox.Text = string.Empty;
            packageViewController.UpdatePackageFeed();
        }

        private void refreshButton_Click(object sender, EventArgs e)
        {
            UpdateSelectedRepository();
        }

        private void settingsButton_Click(object sender, EventArgs e)
        {
            Hide();
            if (packageViewController.ShowPackageSourceConfigurationDialog() == DialogResult.OK)
            {
                UpdateSelectedRepository();
            }
            Show();
        }

        private void closeButton_Click(object sender, EventArgs e)
        {
            Close();
        }
    }
}
