﻿using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using EnvDTE;
using JetBrains.Annotations;
using Malware.MDKServices;
using MDK.Commands;
using MDK.Services;
using MDK.Views;
using MDK.VisualStudio;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace MDK
{
    /// <summary>
    /// The MDK Visual Studio Extension
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true)]
    [InstalledProductRegistration("#110", "#112", "1.0", IconResourceID = 400)] // Info on this package for Help/About
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(PackageGuidString)]
    [SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1650:ElementDocumentationMustBeSpelledCorrectly", Justification = "pkgdef, VS and vsixmanifest are valid VS terms")]
    [ProvideOptionPage(typeof(MDKOptions), "MDK/SE", "Options", 0, 0, true)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists)]
    public sealed partial class MDKPackage : ExtendedPackage, IVsSolutionEvents
    {
        /// <summary>
        /// RunMDKToolCommandPackage GUID string.
        /// </summary>
        public const string PackageGuidString = "7b9c2d3e-b001-4a3e-86a8-00dc6f2af032";

        uint _solutionEventsCookie;
        bool _hasCheckedForUpdates;

        /// <summary>
        /// Creates a new instance of <see cref="MDKPackage" />
        /// </summary>
        public MDKPackage()
        {
            ScriptUpgrades = new ScriptUpgrades();
        }

        /// <summary>
        /// Gets the MDK options
        /// </summary>
        public MDKOptions Options => (MDKOptions)GetDialogPage(typeof(MDKOptions));

        /// <summary>
        /// The service provider
        /// </summary>
        public IServiceProvider ServiceProvider => this;

        /// <summary>
        /// The <see cref="ScriptUpgrades"/> service
        /// </summary>
        public ScriptUpgrades ScriptUpgrades { get; }

        /// <summary>
        /// Gets the installation path for the current MDK package
        /// </summary>
        public DirectoryInfo InstallPath { get; } = new FileInfo(new Uri(typeof(MDKPackage).Assembly.CodeBase).LocalPath).Directory;

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the initialization code that rely on services provided by VisualStudio.
        /// </summary>
        protected override void Initialize()
        {
            // Make sure the dialog page is loaded, since the options are needed in other threads and not preloading it 
            // here will cause a threading exception.
            GetDialogPage(typeof(MDKOptions));

            KnownUIContexts.SolutionExistsAndFullyLoadedContext.WhenActivated(OnFirstSolutionLoaded);

            AddCommand(
                new DeployScriptCommand(this),
                new ScriptOptionsCommand(this),
                new RefreshWhitelistCacheCommand(this),
                new CheckForUpdatesCommand(this)
            );

            base.Initialize();
        }

        async void CheckForUpdates()
        {
            if (!Options.NotifyUpdates)
                return;
            // ReSharper disable once ConditionIsAlwaysTrueOrFalse
            var version = await CheckForUpdates(Options.NotifyPrereleaseUpdates || IsPrerelease);
            if (version != null)
                OnUpdateDetected(version);
        }

        /// <summary>
        /// Checks the GitHub sites for any updated releases.
        /// </summary>
        /// <returns>The newest release version on GitHub, or <c>null</c> if the current version is the latest</returns>
        public async Task<Version> CheckForUpdates(bool includePrerelease)
        {
            try
            {
                var client = new GitHub("malware-dev", "mdk-se", "mdk-se");
                var latestRelease = (await client.ReleasesAsync())
                    .Where(release => !string.IsNullOrWhiteSpace(release.TagName) && (!release.Prerelease || includePrerelease))
                    .OrderByDescending(r => r.PublishedAt)
                    .FirstOrDefault();
                if (latestRelease == null)
                    return null;

                var match = Regex.Match(latestRelease.TagName, @"\d+\.\d+(\.\d+)?");
                if (match.Success)
                {
                    var detectedVersion = new Version(match.Value);
                    if (detectedVersion > Version)
                        return detectedVersion;
                }
                return null;
            }
            catch (Exception e)
            {
                LogPackageError("CheckForUpdates", e);
                // We don't want to make a fuss about this.
                return null;
            }
        }

        void OnUpdateDetected(Version detectedVersion)
        {
            if (detectedVersion == null)
                return;
            UpdateDetectedDialog.ShowDialog(new UpdateDetectedDialogModel(detectedVersion));
        }

        async void OnFirstSolutionLoaded()
        {
            await AnalyzeSolution();
            KnownUIContexts.ShellInitializedContext.WhenActivated(OnShellActivated);
            KnownUIContexts.SolutionExistsAndFullyLoadedContext.UIContextChanged += SolutionExistsAndFullyLoadedContextOnUIContextChanged;
        }

        void OnShellActivated()
        {
            var solutionCtl = (IVsSolution)GetGlobalService(typeof(SVsSolution));
            solutionCtl.AdviseSolutionEvents(this, out _solutionEventsCookie);
        }

        async void OnProjectLoaded(Project project)
        {
            if (ScriptUpgrades.IsBusy)
                return;
            var result = await ScriptUpgrades.AnalyzeAsync(project, new ScriptUpgradeAnalysisOptions
            {
                DefaultGameBinPath = Options.GetActualGameBinPath(),
                InstallPath = InstallPath.FullName,
                TargetVersion = Version,
                GameAssemblyNames = GameAssemblyNames,
                GameFiles = GameFiles,
                UtilityAssemblyNames = UtilityAssemblyNames,
                UtilityFiles = UtilityFiles
            });
            if (!result.HasScriptProjects || result.IsValid)
                return;

            QueryUpgrade(this, result);
        }

        void QueryUpgrade([NotNull] MDKPackage package, ScriptSolutionAnalysisResult result)
        {
            if (package == null)
                throw new ArgumentNullException(nameof(package));
            var model = new RequestUpgradeDialogModel(package, result);
            RequestUpgradeDialog.ShowDialog(model);
        }

        async void SolutionExistsAndFullyLoadedContextOnUIContextChanged(object sender, UIContextChangedEventArgs e)
        {
            if (e.Activated)
                await AnalyzeSolution();
        }

        async Task AnalyzeSolution()
        {
            var result = await ScriptUpgrades.AnalyzeAsync(DTE.Solution, new ScriptUpgradeAnalysisOptions
            {
                DefaultGameBinPath = Options.GetActualGameBinPath(),
                InstallPath = InstallPath.FullName,
                TargetVersion = Version,
                GameAssemblyNames = GameAssemblyNames,
                GameFiles = GameFiles,
                UtilityAssemblyNames = UtilityAssemblyNames,
                UtilityFiles = UtilityFiles
            });
            if (!result.HasScriptProjects)
                return;

            if (!result.IsValid)
                QueryUpgrade(this, result);

            if (!_hasCheckedForUpdates)
            {
                _hasCheckedForUpdates = true;
                CheckForUpdates();
            }
        }

        int IVsSolutionEvents.OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded)
        {
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel)
        {
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved)
        {
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy)
        {
            pRealHierarchy.GetProperty(VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_ExtObject, out object objProj);
            OnProjectLoaded((Project)objProj);
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel)
        {
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy)
        {
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnQueryCloseSolution(object pUnkReserved, ref int pfCancel)
        {
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnBeforeCloseSolution(object pUnkReserved)
        {
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnAfterCloseSolution(object pUnkReserved)
        {
            return VSConstants.S_OK;
        }
    }
}
