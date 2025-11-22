using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using DunGen;
using HarmonyLib;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

namespace RestoreMapper
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    [BepInDependency(GUID_LOBBY_COMPATIBILITY, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        internal const string PLUGIN_GUID = "butterystancakes.lethalcompany.restoremapper", PLUGIN_NAME = "Restore Mapper", PLUGIN_VERSION = "1.3.2";
        internal static new ManualLogSource Logger;
        internal static ConfigEntry<bool> configLowQuality, configShowPath;

        const string GUID_LOBBY_COMPATIBILITY = "BMX.LobbyCompatibility";

        void Awake()
        {
            Logger = base.Logger;

            if (Chainloader.PluginInfos.ContainsKey(GUID_LOBBY_COMPATIBILITY))
            {
                Logger.LogInfo("CROSS-COMPATIBILITY - Lobby Compatibility detected");
                LobbyCompatibility.Init();
            }

            configShowPath = Config.Bind(
                "Gameplay",
                "Show Path",
                false,
                "Should the mapper draw a line back to main entrance for its user?");

            configLowQuality = Config.Bind(
                "Performance",
                "Low Quality",
                false,
                "Decreases the resolution of the mapper's image, to match the radar camera.\nThis will reduce memory usage and might also reduce lag spikes when activating the device.");

            new Harmony(PLUGIN_GUID).PatchAll();

            RenderPipelineManager.beginCameraRendering += RenderingOverrides.OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering += RenderingOverrides.OnEndCameraRendering;

            Logger.LogInfo($"{PLUGIN_NAME} v{PLUGIN_VERSION} loaded");
        }
    }

    [HarmonyPatch]
    class RestoreMapperPatches
    {
        internal static Vector3 mainEntrancePos;
        internal static Bounds mineStartBounds;

        [HarmonyPatch(typeof(Terminal), nameof(Terminal.Awake))]
        [HarmonyPostfix]
        static void Terminal_Post_Awake(Terminal __instance)
        {
            TerminalKeyword buy = __instance.terminalNodes?.allKeywords.FirstOrDefault(keyword => keyword.name == "Buy");
            TerminalNode buyMapper = buy?.compatibleNouns.FirstOrDefault(compatibleNoun => compatibleNoun.noun?.name == "Mapper").result;
            CompatibleNoun confirm = buyMapper?.terminalOptions.FirstOrDefault(compatibleNoun => compatibleNoun.noun?.name == "Confirm");
            List<Item> itemsList = StartOfRound.Instance?.allItemsList?.itemsList;
            Item mapperTool = itemsList?.FirstOrDefault(item => item.name == "MapDevice");

            if (buy == null || buyMapper == null || confirm == null || itemsList == null || mapperTool == null)
            {
                Plugin.Logger.LogError("Encountered an error while caching essential references. Loading will be skipped.");
                return;
            }

            // add the mapper to the list of shop items
            __instance.buyableItemsList = __instance.buyableItemsList.AddItem(mapperTool).ToArray();
            buyMapper.buyItemIndex = __instance.buyableItemsList.Length - 1;
            confirm.result.buyItemIndex = buyMapper.buyItemIndex;
            Plugin.Logger.LogDebug("Assigned IDs");

            // fix the price to match
            mapperTool.creditsWorth = 150;
            Plugin.Logger.LogDebug("Assigned price");

            // add missing SFX
            Item proFlashlight = itemsList.FirstOrDefault(item => item.name == "ProFlashlight");
            if (proFlashlight != null)
            {
                mapperTool.grabSFX = proFlashlight.grabSFX;
                mapperTool.dropSFX = proFlashlight.dropSFX;
                mapperTool.pocketSFX = proFlashlight.pocketSFX;
                Plugin.Logger.LogDebug("Assigned SFX");
            }
            else
                Plugin.Logger.LogWarning("Couldn't find pro-flashlight item. Sound effects will be missing.");

            // inventory icon
            try
            {
                AssetBundle mapperBundle = AssetBundle.LoadFromFile(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "restoremapper"));
                mapperTool.itemIcon = mapperBundle.LoadAsset<Sprite>("MapperIcon");
                mapperBundle.Unload(false);
                Plugin.Logger.LogDebug("Assigned special icon");
            }
            catch
            {
                Plugin.Logger.LogWarning("Encountered some error loading asset bundle. Inventory icon will be incorrect.");
                Item cardboardBox = itemsList?.FirstOrDefault(item => item.itemIcon?.name == "caticontest");
                if (cardboardBox != null)
                    mapperTool.itemIcon = cardboardBox.itemIcon;
            }
        }

        // prevent a memory leak
        [HarmonyPatch(typeof(MapDevice), nameof(MapDevice.Start))]
        [HarmonyPrefix]
        static bool MapDevice_Pre_Start(MapDevice __instance)
        {
            if (__instance.mapCamera != null)
            {
                Plugin.Logger.LogWarning($"Mapper #{__instance.GetInstanceID()} tried to call Start() more than once, this is dangerous and would've caused a memory leak");
                return false;
            }

            return true;
        }

        [HarmonyPatch(typeof(MapDevice), nameof(MapDevice.Start))]
        [HarmonyPostfix]
        static void MapDevice_Post_Start(MapDevice __instance)
        {
            if (!__instance.mapCamera.CompareTag("MapCamera"))
                return;

            // copy the camera object
            __instance.mapCamera = Object.Instantiate(__instance.mapCamera.gameObject, __instance.mapCamera.transform.parent).GetComponent<Camera>();
            __instance.mapCamera.tag = "Untagged";

            // copy the render texture
            RenderTexture orig = __instance.mapCamera.targetTexture;
            int width = 655, height = 455;
            if (Plugin.configLowQuality.Value)
            {
                width = orig.width;
                height = orig.height;
            }
            __instance.mapCamera.targetTexture = new(width, height, orig.depth, orig.format);
            Plugin.Logger.LogDebug($"Mapper #{__instance.GetInstanceID()} camera and texture cloned");

            // get refs
            MapperScreen mapperScreen = __instance.mapCamera.gameObject.AddComponent<MapperScreen>();
            mapperScreen.cam = __instance.mapCamera;
            __instance.mapLight = __instance.mapCamera.GetComponentInChildren<Light>();
            mapperScreen.light = __instance.mapLight;
            __instance.mapAnimatorTransition = __instance.mapCamera.GetComponentInChildren<Animator>();
            mapperScreen.transition = __instance.mapAnimatorTransition.GetComponent<Renderer>();

            // performance
            __instance.mapCamera.gameObject.SetActive(false);
            __instance.mapCamera.enabled = true;

            // set up the light
            __instance.mapLight.enabled = false;
            mapperScreen.transition.forceRenderingOff = true;
            Plugin.Logger.LogDebug($"Mapper #{__instance.GetInstanceID()} light setup");

            // assign texture to screen
            MeshRenderer rend = __instance.GetComponentInChildren<MeshRenderer>();
            Material[] mats = rend.materials;
            mats.FirstOrDefault(mat => mat.name.StartsWith("MapScreen")).mainTexture = __instance.mapCamera.targetTexture;
            rend.materials = mats;
            Plugin.Logger.LogDebug($"Mapper #{__instance.GetInstanceID()} screen retextured");

            // minor fix - the green flash should cover the whole screen
            __instance.mapAnimatorTransition.transform.localPosition = new(0f, 0f, -0.95f);
        }

        [HarmonyPatch(typeof(MapDevice), nameof(MapDevice.ItemActivate))]
        [HarmonyPrefix]
        static bool MapDevice_Pre_ItemActivate(MapDevice __instance/*, bool used, bool buttonDown*/)
        {
            if (__instance.playerHeldBy == null || __instance.mapCamera.CompareTag("MapCamera"))
                return true;

            if (__instance.pingMapCoroutine != null)
                __instance.StopCoroutine(__instance.pingMapCoroutine);

            if (__instance.mapCamera.TryGetComponent(out MapperScreen mapperScreen))
                mapperScreen.target = __instance.playerHeldBy.transform;
            __instance.StartCoroutine(pingMapSystem(__instance));

            // base.ItemActivate(used, buttonDown);
            return false;
        }

        [HarmonyPatch(typeof(NetworkBehaviour), nameof(NetworkBehaviour.OnDestroy))]
        [HarmonyPostfix]
        static void NetworkBehaviour_Post_OnDestroy(NetworkBehaviour __instance)
        {
            if (__instance is MapDevice mapDevice && mapDevice.mapCamera != null && !mapDevice.mapCamera.CompareTag("MapCamera"))
            {
                // clean up created objects
                if (mapDevice.mapCamera.targetTexture.IsCreated())
                    mapDevice.mapCamera.targetTexture.Release();
                Object.Destroy(mapDevice.mapCamera.targetTexture);
                Object.Destroy(mapDevice.mapCamera.gameObject);
                Plugin.Logger.LogDebug($"Mapper #{__instance.GetInstanceID()} cleaned up");
            }
        }

        [HarmonyPatch(typeof(GrabbableObject), nameof(GrabbableObject.UseItemBatteries))]
        [HarmonyPrefix]
        static bool GrabbableObject_Pre_UseItemBatteries(GrabbableObject __instance)
        {
            // can't be used in orbit
            return __instance is not MapDevice || !StartOfRound.Instance.mapScreen.overrideCameraForOtherUse;
        }

        static IEnumerator pingMapSystem(MapDevice mapDevice)
        {
            mapDevice.mapCamera.gameObject.SetActive(true);
            mapDevice.mapAnimatorTransition.SetTrigger("Transition");
            yield return new WaitForSeconds(0.235f);
            mapDevice.mapCamera.gameObject.SetActive(false);
            yield break;
        }

        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.SwitchMapMonitorPurpose))]
        [HarmonyPostfix]
        static void StartOfRound_Post_SwitchMapMonitorPurpose(bool displayInfo)
        {
            // reset screens in orbit
            if (displayInfo)
            {
                RenderTexture rt = RenderTexture.active;
                MapDevice[] mapDevices = Object.FindObjectsByType<MapDevice>(FindObjectsSortMode.None);
                foreach (MapDevice mapDevice in mapDevices)
                {
                    if (mapDevice.mapCamera != null && !mapDevice.mapCamera.CompareTag("MapCamera"))
                    {
                        mapDevice.mapCamera.gameObject.SetActive(false);
                        RenderTexture.active = mapDevice.mapCamera.targetTexture;
                        GL.Clear(true, true, Color.clear);
                    }
                }
                RenderTexture.active = rt;
            }
        }

        [HarmonyPatch(typeof(MapDevice), nameof(MapDevice.EquipItem))]
        [HarmonyPostfix]
        static void MapDevice_Post_EquipItem(MapDevice __instance)
        {
            __instance.playerHeldBy.equippedUsableItemQE = false;
        }

        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.FinishGeneratingNewLevelClientRpc))]
        [HarmonyPostfix]
        static void RoundManager_Post_FinishGeneratingNewLevelClientRpc(RoundManager __instance)
        {
            // https://github.com/ButteryStancakes/ButteryFixes/blob/da6acfca597c431ba0249a121883b8fae5574b07/Patches/General/RoundManagerPatches.cs#L190
            EntranceTeleport mainEntrance = Object.FindObjectsByType<EntranceTeleport>(FindObjectsSortMode.None).FirstOrDefault(teleport => teleport.entranceId == 0 && !teleport.isEntranceToBuilding);
            mainEntrancePos = mainEntrance != null ? mainEntrance.entrancePoint.position : Vector3.zero;

            if (__instance.currentDungeonType == 4)
            {
                GameObject dungeonRoot = __instance.dungeonGenerator?.Root ?? GameObject.Find("/Systems/LevelGeneration/LevelGenerationRoot");
                if (dungeonRoot == null)
                {
                    if (StartOfRound.Instance.currentLevel.name != "CompanyBuildingLevel")
                        Plugin.Logger.LogWarning("Landed on a moon with no dungeon generated. This shouldn't happen");

                    return;
                }

                foreach (Tile tile in dungeonRoot.GetComponentsInChildren<Tile>())
                {
                    if (tile.name.StartsWith("MineshaftStartTile"))
                    {
                        // reused and adapted from Mask Fixes
                        // https://github.com/ButteryStancakes/MaskFixes/blob/165601d81827d368c119f12ced2bf8fdaffbeec8/Patches.cs#L621

                        // calculate the bounds of the elevator start room
                        // center:  ( -1,   51.37,  3.2 )
                        // size:    ( 30,      20,   15 )
                        Vector3[] corners =
                        [
                            new(-16f, 41.37f, -4.3f),
                            new(14f, 41.37f, -4.3f),
                            new(-16f, 61.37f, -4.3f),
                            new(14f, 61.37f, -4.3f),
                            new(-16f, 41.37f, 10.7f),
                            new(14f, 41.37f, 10.7f),
                            new(-16f, 61.37f, 10.7f),
                            new(14f, 61.37f, 10.7f),
                        ];
                        tile.transform.TransformPoints(corners);

                        // thanks Zaggy
                        Vector3 min = corners[0], max = corners[0];
                        for (int i = 1; i < corners.Length; i++)
                        {
                            min = Vector3.Min(min, corners[i]);
                            max = Vector3.Max(max, corners[i]);
                        }

                        mineStartBounds = new()
                        {
                            min = min,
                            max = max
                        };
                        Plugin.Logger.LogDebug("Calculated bounds for mineshaft elevator's start room");
                    }
                }
            }
        }
    }

    internal class RenderingOverrides
    {
        static MapperScreen currentScreen;
        static bool? lineNotRendering;

        public static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            ResetRendering();

            currentScreen = camera.GetComponent<MapperScreen>();
            if (currentScreen != null)
            {
                currentScreen.light.enabled = true;
                currentScreen.transition.forceRenderingOff = false;
                currentScreen.line.forceRenderingOff = !currentScreen.drawPath;

                if (StartOfRound.Instance?.mapScreen != null)
                {
                    // inside the building (or at the company)
                    if (StartOfRound.Instance.currentLevelID == 3 || camera.transform.position.y < -80f)
                    {
                        camera.nearClipPlane = StartOfRound.Instance.mapScreen.cameraNearPlane;
                        camera.farClipPlane = Mathf.Max(StartOfRound.Instance.mapScreen.cameraFarPlane, 7.52f);
                    }
                    else
                    {
                        // increased so it looks "correct" when using UniversalRadar
                        camera.nearClipPlane = -22.47f;
                        camera.farClipPlane = 27.52f;
                    }

                    lineNotRendering = StartOfRound.Instance.mapScreen.lineFromRadarTargetToExit.forceRenderingOff;
                    StartOfRound.Instance.mapScreen.lineFromRadarTargetToExit.forceRenderingOff = true;
                }
            }
        }

        public static void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            ResetRendering();
        }

        static void ResetRendering()
        {
            if (lineNotRendering.HasValue)
            {
                if (StartOfRound.Instance?.mapScreen?.lineFromRadarTargetToExit != null)
                    StartOfRound.Instance.mapScreen.lineFromRadarTargetToExit.forceRenderingOff = (bool)lineNotRendering;

                lineNotRendering = null;
            }

            if (currentScreen != null)
            {
                currentScreen.light.enabled = false;
                currentScreen.transition.forceRenderingOff = true;
                currentScreen.line.forceRenderingOff = true;
            }
        }
    }
}