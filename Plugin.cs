using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using GameNetcodeStuff;
using HarmonyLib;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace RestoreMapper
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    [BepInDependency(GUID_LOBBY_COMPATIBILITY, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        internal const string PLUGIN_GUID = "butterystancakes.lethalcompany.restoremapper", PLUGIN_NAME = "Restore Mapper", PLUGIN_VERSION = "1.2.2";
        internal static new ManualLogSource Logger;
        internal static ConfigEntry<bool> configLowQuality;

        const string GUID_LOBBY_COMPATIBILITY = "BMX.LobbyCompatibility";

        void Awake()
        {
            Logger = base.Logger;

            if (Chainloader.PluginInfos.ContainsKey(GUID_LOBBY_COMPATIBILITY))
            {
                Logger.LogInfo("CROSS-COMPATIBILITY - Lobby Compatibility detected");
                LobbyCompatibility.Init();
            }

            configLowQuality = Config.Bind(
                "Performance",
                "Low Quality",
                false,
                "Decreases the resolution of the mapper image (to match the radar camera) and disables film grain.\nThis will reduce memory usage and might also reduce lag spikes when activating the device.");

            new Harmony(PLUGIN_GUID).PatchAll();

            Logger.LogInfo($"{PLUGIN_NAME} v{PLUGIN_VERSION} loaded");
        }
    }

    [HarmonyPatch]
    class RestoreMapperPatches
    {
        static Texture scanline;

        [HarmonyPatch(typeof(Terminal), "Awake")]
        [HarmonyPostfix]
        static void TerminalPostAwake(Terminal __instance)
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
                scanline = mapperBundle.LoadAsset<Texture>("scanline");
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
        static bool MapDevicePreStart(MapDevice __instance)
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
        static void MapDevicePostStart(MapDevice __instance)
        {
            if (!__instance.mapCamera.CompareTag("MapCamera"))
                return;

            // copy the camera object
            RenderTexture orig = __instance.mapCamera.targetTexture;
            __instance.mapCamera = Object.Instantiate(__instance.mapCamera.gameObject, __instance.mapCamera.transform.parent).GetComponent<Camera>();
            __instance.mapCamera.tag = "Untagged";
            __instance.mapCamera.targetTexture = new(Plugin.configLowQuality.Value ? orig.width : 655, Plugin.configLowQuality.Value ? orig.height : 455, orig.depth, orig.format);
            Plugin.Logger.LogDebug($"Mapper #{__instance.GetInstanceID()} cam&tex cloned");

            // get refs
            __instance.mapAnimatorTransition = __instance.mapCamera.GetComponentInChildren<Animator>();
            __instance.mapAnimatorTransition.transform.localPosition = new(0f, 0f, -0.95f);
            __instance.mapLight = __instance.mapCamera.GetComponentInChildren<Light>();

            // performance
            __instance.mapCamera.gameObject.SetActive(false);
            __instance.mapCamera.enabled = true;

            // set up the light
            __instance.mapLight.enabled = false;
            Plugin.Logger.LogDebug($"Mapper #{__instance.GetInstanceID()} light setup");

            // assign texture to screen
            MeshRenderer rend = __instance.GetComponentInChildren<MeshRenderer>();
            Material[] mats = rend.materials;
            mats.FirstOrDefault(mat => mat.name.StartsWith("MapScreen")).mainTexture = __instance.mapCamera.targetTexture;
            rend.materials = mats;
            Plugin.Logger.LogDebug($"Mapper #{__instance.GetInstanceID()} screen retex'd");

            // post processing from earlier versions
            if (!Plugin.configLowQuality.Value)
            {
                VolumeProfile profile = __instance.mapCamera.GetComponentInChildren<Volume>()?.profile;
                profile.TryGet(out FilmGrain filmGrain);
                if (filmGrain == null)
                    filmGrain = profile.Add<FilmGrain>();
                filmGrain.type.Override(FilmGrainLookup.Custom);
                filmGrain.texture.Override(scanline);
                filmGrain.intensity.Override(1f);
                filmGrain.response.Override(1f);
            }
        }

        [HarmonyPatch(typeof(MapDevice), nameof(MapDevice.ItemActivate))]
        [HarmonyPrefix]
        static bool MapDeviceItemActivate(MapDevice __instance, /*bool used, bool buttonDown,*/ Coroutine ___pingMapCoroutine)
        {
            if (__instance.playerHeldBy == null || __instance.mapCamera.CompareTag("MapCamera"))
                return true;

            if (___pingMapCoroutine != null)
                __instance.StopCoroutine(___pingMapCoroutine);

            __instance.StartCoroutine(pingMapSystem(__instance, __instance.playerHeldBy));

            // base.ItemActivate(used, buttonDown);
            return false;
        }

        [HarmonyPatch(typeof(NetworkBehaviour), nameof(NetworkBehaviour.OnDestroy))]
        [HarmonyPostfix]
        static void NetworkBehaviourPostOnDestroy(NetworkBehaviour __instance)
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
        static bool GrabbableObjectPreUseItemBatteries(GrabbableObject __instance)
        {
            // can't be used in orbit, or if holding player is in the ship
            return __instance is not MapDevice || (!StartOfRound.Instance.mapScreen.overrideCameraForOtherUse && (__instance.playerHeldBy == null || ((!__instance.playerHeldBy.isInHangarShipRoom && !__instance.playerHeldBy.isInElevator) || !StartOfRound.Instance.mapScreen.cam.enabled)));
        }

        static IEnumerator pingMapSystem(MapDevice mapDevice, PlayerControllerB playerHeldBy)
        {
            mapDevice.mapCamera.gameObject.SetActive(true);
            if (playerHeldBy == GameNetworkManager.Instance.localPlayerController && ((!GameNetworkManager.Instance.localPlayerController.isInHangarShipRoom && !GameNetworkManager.Instance.localPlayerController.isInElevator) || !StartOfRound.Instance.mapScreen.cam.enabled))
            {
                mapDevice.mapAnimatorTransition.SetTrigger("Transition");
                StartOfRound.Instance.mapScreenPlayerName.gameObject.SetActive(false);
            }
            yield return new WaitForSeconds(0.035f);
            Vector3 playerPos = playerHeldBy.transform.position;
            playerPos.y += 3.636f;
            mapDevice.mapCamera.transform.position = playerPos;
            yield return new WaitForSeconds(0.2f);
            mapDevice.mapLight.enabled = playerHeldBy.isInsideFactory || playerHeldBy.transform.position.y < -80f;
            mapDevice.mapCamera.Render();
            mapDevice.mapLight.enabled = false;
            mapDevice.mapCamera.gameObject.SetActive(false);
            StartOfRound.Instance.mapScreenPlayerName.gameObject.SetActive(true);
            yield break;
        }

        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.SwitchMapMonitorPurpose))]
        [HarmonyPostfix]
        static void PostSwitchMapMonitorPurpose(bool displayInfo)
        {
            // reset screens in orbit
            if (displayInfo)
            {
                foreach (MapDevice mapDevice in Object.FindObjectsByType<MapDevice>(FindObjectsSortMode.None))
                {
                    if (mapDevice.mapCamera != null && !mapDevice.mapCamera.CompareTag("MapCamera"))
                    {
                        RenderTexture rt = RenderTexture.active;
                        RenderTexture.active = mapDevice.mapCamera.targetTexture;
                        GL.Clear(true, true, Color.clear);
                        RenderTexture.active = rt;
                    }
                }
            }
        }

        [HarmonyPatch(typeof(MapDevice), nameof(MapDevice.EquipItem))]
        [HarmonyPostfix]
        static void MapDevicePostEquipItem(MapDevice __instance)
        {
            __instance.playerHeldBy.equippedUsableItemQE = false;
        }
    }
}