﻿using Bonsai.Configuration;
using Bonsai.Design;
using NuGet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;

namespace Bonsai
{
    sealed class DependencyInspector : MarshalByRefObject
    {
        readonly PackageConfiguration packageConfiguration;
        const string BonsaiExtension = ".bonsai";
        const string LayoutExtension = ".layout";
        const string RepositoryPath = "Packages";
        const string ExtensionTypeNodeName = "ExtensionTypes";
        const string TypeNodeName = "Type";

        public DependencyInspector(PackageConfiguration configuration)
        {
            ConfigurationHelper.SetAssemblyResolve(configuration);
            packageConfiguration = configuration;
        }

        IEnumerable<VisualizerDialogSettings> GetVisualizerSettings(VisualizerLayout root)
        {
            var stack = new Stack<VisualizerLayout>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var layout = stack.Pop();
                foreach (var settings in layout.DialogSettings)
                {
                    yield return settings;
                    var editorSettings = settings as WorkflowEditorSettings;
                    if (editorSettings != null)
                    {
                        stack.Push(editorSettings.EditorVisualizerLayout);
                    }
                }
            }
        }

        Configuration.PackageReference[] GetWorkflowPackageDependencies(string path)
        {
            var assemblies = new HashSet<Assembly>();
            using (var reader = XmlReader.Create(path))
            {
                reader.ReadToFollowing(ExtensionTypeNodeName);
                reader.ReadStartElement();

                assemblies.Add(typeof(WorkflowBuilder).Assembly);
                while (reader.ReadToNextSibling(TypeNodeName))
                {
                    var typeName = reader.ReadElementString();
                    var type = Type.GetType(typeName, false);
                    if (type == null)
                    {
                        throw new InvalidOperationException("The specified workflow has unresolved dependencies.");
                    }

                    assemblies.Add(type.Assembly);
                }
            }

            var layoutPath = Path.ChangeExtension(path, BonsaiExtension + LayoutExtension);
            if (File.Exists(layoutPath))
            {
                var layoutSerializer = new XmlSerializer(typeof(VisualizerLayout));
                var visualizerMap = new Lazy<IDictionary<string, Type>>(() =>
                    TypeVisualizerLoader.GetTypeVisualizerDictionary(packageConfiguration)
                                        .Select(descriptor => descriptor.VisualizerTypeName).Distinct()
                                        .Select(typeName => Type.GetType(typeName, false))
                                        .Where(type => type != null)
                                        .ToDictionary(type => type.FullName)
                                        .Wait());

                using (var reader = XmlReader.Create(layoutPath))
                {
                    var layout = (VisualizerLayout)layoutSerializer.Deserialize(reader);
                    foreach (var settings in GetVisualizerSettings(layout))
                    {
                        Type type;
                        var typeName = settings.VisualizerTypeName;
                        if (typeName == null) continue;
                        if (!visualizerMap.Value.TryGetValue(typeName, out type))
                        {
                            throw new InvalidOperationException("The specified workflow has unresolved visualizer dependencies.");
                        }

                        assemblies.Add(type.Assembly);
                    }
                }
            }

            var placeholderRepository = new LocalPackageRepository(RepositoryPath);
            var pathResolver = new PackageManager(placeholderRepository, RepositoryPath).PathResolver;
            var packageMap = new Dictionary<string, Configuration.PackageReference>();
            foreach (var package in packageConfiguration.Packages)
            {
                var packagePath = pathResolver.GetPackageDirectory(package.Id, SemanticVersion.Parse(package.Version));
                packageMap.Add(packagePath, package);
            }

            var dependencies = new List<Configuration.PackageReference>();
            foreach (var assembly in assemblies)
            {
                var assemblyName = assembly.GetName().Name;
                var assemblyLocation = ConfigurationHelper.GetAssemblyLocation(packageConfiguration, assemblyName);
                if (assemblyLocation != null)
                {
                    var pathElements = assemblyLocation.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (pathElements.Length > 1 && pathElements[0] == RepositoryPath)
                    {
                        Configuration.PackageReference package;
                        if (packageMap.TryGetValue(pathElements[1], out package))
                        {
                            dependencies.Add(package);
                        }
                    }
                }
            }

            return dependencies.ToArray();
        }

        public static IObservable<PackageDependency> GetWorkflowPackageDependencies(string path, PackageConfiguration configuration)
        {
            if (!File.Exists(path))
            {
                throw new ArgumentException("Invalid workflow path.", "path");
            }

            if (configuration == null)
            {
                throw new ArgumentNullException("configuration");
            }

            return Observable.Using(
                () => new LoaderResource<DependencyInspector>(configuration),
                resource => from dependency in resource.Loader.GetWorkflowPackageDependencies(path).ToObservable()
                            let versionSpec = new VersionSpec
                            {
                                MinVersion = SemanticVersion.Parse(dependency.Version),
                                IsMinInclusive = true
                            }
                            select new PackageDependency(dependency.Id, versionSpec));
        }
    }
}