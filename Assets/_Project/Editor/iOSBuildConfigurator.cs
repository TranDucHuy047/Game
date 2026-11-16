#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_IOS
using UnityEditor.iOS.Xcode;
#endif

namespace CoralArchipelago.EditorTools
{
    /// <summary>
    /// Configures iOS Player Settings (Metal API, ARM64, IL2CPP, Linear Color, Landscape Orientation)
    /// and automatically injects iOS 14-18 Local Network UDP permissions into Info.plist on Xcode export.
    /// </summary>
    public class iOSBuildConfigurator : IPostprocessBuildWithReport
    {
        public int callbackOrder => 100;

        [MenuItem("Coral Archipelago/Configure iOS Player Settings (Metal + LAN UDP)")]
        public static void ApplyRecommendediOSPlayerSettings()
        {
            PlayerSettings.productName = "Coral Archipelago";
            PlayerSettings.companyName = "CoralStudio";
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.iOS, "com.coralarchipelago.pve");
            PlayerSettings.colorSpace = ColorSpace.Linear;

            PlayerSettings.SetScriptingBackend(NamedBuildTarget.iOS, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetArchitecture(NamedBuildTarget.iOS, 1); // 1 = ARM64

            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.iOS, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.iOS, new[] { GraphicsDeviceType.Metal });

            PlayerSettings.iOS.targetOSVersionString = "15.0";
            PlayerSettings.iOS.appleEnableAutomaticSigning = true;
            PlayerSettings.accelerometerFrequency = 0;

            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;

            Debug.Log("[iOSBuildConfigurator] Applied iOS Metal + ARM64 + Linear + Landscape settings.");
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.iOS)
                return;

#if UNITY_IOS
            string outputPath = report.summary.outputPath;

            // 1. Inject iOS 14-18 Local Network UDP permission into Info.plist
            string plistPath = Path.Combine(outputPath, "Info.plist");
            if (File.Exists(plistPath))
            {
                PlistDocument plist = new PlistDocument();
                plist.ReadFromFile(plistPath);

                PlistElementDict root = plist.root;
                root.SetString(
                    "NSLocalNetworkUsageDescription",
                    "Allow Coral Archipelago to connect over Wi-Fi/UDP to local test hosts or co-op servers.");

                PlistElementArray bonjour = root.CreateArray("NSBonjourServices");
                bonjour.AddString("_coral-udp._udp");

                plist.WriteToFile(plistPath);
                Debug.Log("[iOSBuildConfigurator] Injected NSLocalNetworkUsageDescription into Xcode Info.plist.");
            }

            // 2. Disable Bitcode on Xcode 15/16 targets to prevent archive errors
            string pbxPath = PBXProject.GetPBXProjectPath(outputPath);
            if (File.Exists(pbxPath))
            {
                PBXProject pbx = new PBXProject();
                pbx.ReadFromFile(pbxPath);

                string mainTarget = pbx.GetUnityMainTargetGuid();
                string frameworkTarget = pbx.GetUnityFrameworkTargetGuid();
                pbx.SetBuildProperty(mainTarget, "ENABLE_BITCODE", "NO");
                pbx.SetBuildProperty(frameworkTarget, "ENABLE_BITCODE", "NO");

                pbx.WriteToFile(pbxPath);
                Debug.Log("[iOSBuildConfigurator] Disabled ENABLE_BITCODE in Unity-iPhone.xcodeproj.");
            }
#endif
        }
    }
}
#endif
