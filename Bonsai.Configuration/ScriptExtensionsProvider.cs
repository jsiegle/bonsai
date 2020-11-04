﻿using Microsoft.CSharp;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace Bonsai.Configuration
{
    public static class ScriptExtensionsProvider
    {
        static readonly NuGetFramework DefaultFramework = NuGetFramework.ParseFolder("net472");
        const string OutputAssemblyName = "Extensions";
        const string ProjectExtension = ".csproj";
        const string ScriptExtension = "*.cs";
        const string DllExtension = ".dll";

        static IEnumerable<string> FindAssemblyReferences(
            DependencyInfoResource dependencyResource,
            FindLocalPackagesResource localPackageResource,
            SourceCacheContext cacheContext,
            string packageId)
        {
            var packageInfo = localPackageResource.FindPackagesById(packageId, NullLogger.Instance, CancellationToken.None).FirstOrDefault();
            if (packageInfo == null) yield break;
            using (var reader = packageInfo.GetReader())
            {
                var nearestFramework = reader.GetReferenceItems().GetNearest(DefaultFramework);
                if (nearestFramework != null)
                {
                    foreach (var assembly in nearestFramework.Items)
                    {
                        yield return Path.GetFileName(assembly);
                    }
                }
            }

            var dependencyInfo = dependencyResource.ResolvePackage(packageInfo.Identity, DefaultFramework, cacheContext, NullLogger.Instance, CancellationToken.None).Result;
            foreach (var dependency in dependencyInfo.Dependencies)
            {
                foreach (var reference in FindAssemblyReferences(dependencyResource, localPackageResource, cacheContext, dependency.Id))
                {
                    yield return reference;
                }
            }
        }

        public static ScriptExtensions CompileAssembly(PackageConfiguration configuration, string editorRepositoryPath, bool includeDebugInformation)
        {
            var path = Environment.CurrentDirectory;
            var configurationRoot = ConfigurationHelper.GetConfigurationRoot(configuration);
            var scriptProjectFile = Path.Combine(path, Path.ChangeExtension(OutputAssemblyName, ProjectExtension));
            if (!File.Exists(scriptProjectFile)) return new ScriptExtensions(configuration, null);

            var extensionsPath = Path.Combine(path, OutputAssemblyName);
            if (!Directory.Exists(extensionsPath)) return new ScriptExtensions(configuration, null);

            var scriptFiles = Directory.GetFiles(extensionsPath, ScriptExtension, SearchOption.AllDirectories);
            if (scriptFiles.Length == 0) return new ScriptExtensions(configuration, null);

            var assemblyNames = new HashSet<string>();
            var assemblyDirectory = Path.GetTempPath() + OutputAssemblyName + "." + Guid.NewGuid().ToString();
            var scriptEnvironment = new ScriptExtensions(configuration, assemblyDirectory);
            var packageSource = new PackageSource(editorRepositoryPath);
            var packageRepository = new SourceRepository(packageSource, Repository.Provider.GetCoreV3());
            var dependencyResource = packageRepository.GetResource<DependencyInfoResource>();
            var localPackageResource = packageRepository.GetResource<FindLocalPackagesResource>();
            using (var cacheContext = new SourceCacheContext())
            {
                var projectReferences = from id in scriptEnvironment.GetPackageReferences()
                                        from assemblyReference in FindAssemblyReferences(dependencyResource, localPackageResource, cacheContext, id)
                                        select assemblyReference;
                assemblyNames.AddRange(scriptEnvironment.GetAssemblyReferences());
                assemblyNames.AddRange(projectReferences);
            }

            var assemblyFile = Path.Combine(assemblyDirectory, Path.ChangeExtension(OutputAssemblyName, DllExtension));
            var assemblyReferences = (from fileName in assemblyNames
                                      let assemblyName = Path.GetFileNameWithoutExtension(fileName)
                                      let assemblyLocation = ConfigurationHelper.GetAssemblyLocation(configuration, assemblyName)
                                      select assemblyLocation == null ? fileName :
                                      Path.IsPathRooted(assemblyLocation) ? assemblyLocation :
                                      Path.Combine(configurationRoot, assemblyLocation))
                                      .ToArray();
            var compilerParameters = new CompilerParameters(assemblyReferences, assemblyFile);
            compilerParameters.GenerateExecutable = false;
            compilerParameters.GenerateInMemory = false;
            compilerParameters.IncludeDebugInformation = includeDebugInformation;
            if (!includeDebugInformation)
            {
                compilerParameters.CompilerOptions = "/optimize";
            }

            using (var codeProvider = new CSharpCodeProvider())
            {
                var results = codeProvider.CompileAssemblyFromFile(compilerParameters, scriptFiles);
                if (results.Errors.HasErrors)
                {
                    try
                    {
                        Console.Error.WriteLine("--- Error building script extensions ---");
                        foreach (var error in results.Errors)
                        {
                            Console.Error.WriteLine(error);
                        }
                    }
                    finally { scriptEnvironment.Dispose(); }
                    return new ScriptExtensions(configuration, null);
                }
                else
                {
                    var assemblyName = AssemblyName.GetAssemblyName(assemblyFile);
                    configuration.AssemblyReferences.Add(assemblyName.Name);
                    configuration.AssemblyLocations.Add(assemblyName.Name, ProcessorArchitecture.MSIL, assemblyName.CodeBase);
                    scriptEnvironment.AssemblyName = assemblyName;
                }
                return scriptEnvironment;
            }
        }
    }
}