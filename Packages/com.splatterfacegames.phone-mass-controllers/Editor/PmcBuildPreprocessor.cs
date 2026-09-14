using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Splatter.Pmc.Editor {

    /// <summary>
    /// Copies every <c>PmcHost.ControllerDir</c> under <c>Assets/</c> (plus the
    /// package <c>Web/</c> SDK) into <c>Assets/StreamingAssets/pmc/</c> before a
    /// player build, and removes the staged tree afterwards — see
    /// docs/exporting.md. StreamingAssets is shipped byte-for-byte, so controller
    /// pages survive asset import settings that would otherwise rewrite them.
    ///
    /// Cleanup happens in <see cref="OnPostprocessBuild"/> even when the build
    /// fails; set the env var <c>PMC_KEEP_STREAMINGASSETS=1</c> or the menu toggle
    /// to keep the staged tree (useful when a build pipeline inspects the result).
    /// </summary>
    public sealed class PmcBuildPreprocessor : IPreprocessBuildWithReport, IPostprocessBuildWithReport {
        public int callbackOrder => 0;

        /// <summary>Environment flag that keeps the staged tree after the build.</summary>
        public const string KeepEnvVar = "PMC_KEEP_STREAMINGASSETS";
        const string KeepPref = "Splatter.Pmc.KeepStagedStreamingAssets";
        const string PkgId = "com.splatterfacegames.phone-mass-controllers";
        const string MenuPath = "Splatter/Phone Mass Controllers/Keep staged StreamingAssets after build";

        static string ProjectRoot => Path.GetFullPath(".");
        static string ManifestPath => Path.Combine(ProjectRoot, "Temp", "pmc-staging-manifest.txt");
        static string BackupDir => Path.Combine(ProjectRoot, "Temp", "pmc-staging-backup");

        /// <summary>Keep staged files after the build (env var or menu toggle).</summary>
        public static bool KeepStagedFiles =>
            Environment.GetEnvironmentVariable(KeepEnvVar) == "1" || EditorPrefs.GetBool(KeepPref, false);

        [MenuItem(MenuPath)]
        static void ToggleKeep() => EditorPrefs.SetBool(KeepPref, !EditorPrefs.GetBool(KeepPref, false));

        [MenuItem(MenuPath, true)]
        static bool ToggleKeepValidate() {
            Menu.SetChecked(MenuPath, EditorPrefs.GetBool(KeepPref, false));
            return true;
        }

        public void OnPreprocessBuild(BuildReport report) {
            var dirs = new List<PmcExportStaging.SourceDir>();
            CollectSceneDirs(dirs);
            PmcExportStaging.ScanSourceLiterals(Path.Combine(ProjectRoot, "Assets"), dirs);

            var plan = PmcExportStaging.CreatePlan(dirs, ProjectRoot, WebSdkDir());
            foreach (var w in plan.Warnings)
                Debug.LogWarning("Phone Mass Controllers build: " + w);
            if (plan.Entries.Count == 0)
                return;

            var mf = new PmcExportStaging.Manifest();
            int copied;
            try {
                copied = PmcExportStaging.Stage(plan, ProjectRoot, BackupDir, mf);
                PmcExportStaging.SaveManifest(mf, ManifestPath);
            } catch (Exception e) {
                throw new BuildFailedException("Phone Mass Controllers: staging into StreamingAssets failed: " + e.Message);
            }
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Debug.Log("Phone Mass Controllers: staged " + copied + " controller file(s) into Assets/StreamingAssets from " +
                string.Join("; ", plan.Entries.Select(e => e.DestRel + " (" + e.Reason + ")").ToArray()));
        }

        public void OnPostprocessBuild(BuildReport report) {
            if (!File.Exists(ManifestPath))
                return;
            if (KeepStagedFiles) {
                Debug.Log("Phone Mass Controllers: keeping staged StreamingAssets tree (" + KeepEnvVar + " / menu toggle)");
                return;
            }
            PmcExportStaging.Clean(ManifestPath, ProjectRoot);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        /// <summary>ControllerDirs on PmcHost components in open + build-profile scenes.</summary>
        static void CollectSceneDirs(List<PmcExportStaging.SourceDir> outDirs) {
            var seenScenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var opened = new List<Scene>();
            try {
                for (int i = 0; i < SceneManager.sceneCount; i++) {
                    var s = SceneManager.GetSceneAt(i);
                    if (s.isLoaded && !string.IsNullOrEmpty(s.path) && seenScenes.Add(s.path))
                        ScanScene(s, outDirs);
                }
                var prevActive = SceneManager.GetActiveScene();
                foreach (var es in EditorBuildSettings.scenes) {
                    if (!es.enabled || !seenScenes.Add(es.path)) continue;
                    Scene s;
                    try {
                        s = EditorSceneManager.OpenScene(es.path, OpenSceneMode.Additive);
                    } catch (Exception e) {
                        Debug.LogWarning("Phone Mass Controllers build: couldn't open " + es.path +
                            " for the ControllerDir scan (" + e.Message + ")");
                        continue;
                    }
                    opened.Add(s);
                    ScanScene(s, outDirs);
                }
                if (prevActive.IsValid() && prevActive.isLoaded)
                    SceneManager.SetActiveScene(prevActive);
            } finally {
                foreach (var s in opened)
                    if (s.isLoaded)
                        EditorSceneManager.CloseScene(s, true);
            }
        }

        static void ScanScene(Scene scene, List<PmcExportStaging.SourceDir> outDirs) {
            var file = Path.GetFileName(scene.path);
            foreach (var root in scene.GetRootGameObjects())
                foreach (var host in root.GetComponentsInChildren<PmcHost>(true))
                    if (!string.IsNullOrEmpty(host.ControllerDir))
                        outDirs.Add(new PmcExportStaging.SourceDir {
                            Rel = host.ControllerDir,
                            Reason = "ControllerDir on '" + host.name + "' in " + file
                        });
        }

        /// <summary>The package's Web/ dir — resolved so this works from Library/PackageCache too.</summary>
        static string WebSdkDir() {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(PmcBuildPreprocessor).Assembly);
            if (info != null)
                return Path.Combine(info.resolvedPath, "Web");
            return Path.Combine(ProjectRoot, "Packages", PkgId, "Web");
        }
    }
}
